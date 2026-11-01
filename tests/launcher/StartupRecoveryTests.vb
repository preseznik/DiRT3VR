Imports DiRT3VR
Imports System.IO
Imports System.Text

Module StartupRecoveryTests
    Private ReadOnly Graphics As Byte() = Encoding.UTF8.GetBytes("<hardware_settings_config version='56'><cpu><threadStrategy parallelUpdateRender='true'/></cpu><graphics_card><directx forcedx9='true'/><stereo enabled='true'/><resolution width='1920' height='1080' fullscreen='true' vsync='1' multisampling='4xmsaa'/></graphics_card><audio mixing='original'/></hardware_settings_config>")

    Private Sub Reject(action As Action, check As Action(Of Boolean, String), message As String)
        Dim refused = False
        Try
            action()
        Catch ex As IOException
            refused = True
        End Try
        check(refused, message)
    End Sub

    Private Function Context(root As String, name As String, original As Byte()) As InstallContext
        Dim game = Path.Combine(root, "startup-recovery", name)
        Dim ctx As New InstallContext(game, Path.Combine(root, "startup-users"), Path.Combine(game, "graphics.xml"), Path.Combine(game, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system"))
        Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.WriteAllBytes(ctx.GraphicsPath, Graphics)
        File.WriteAllBytes(Path.Combine(game, "system", "states.bin"), original)
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"))
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        Return ctx
    End Function

    Private Function States(ctx As InstallContext) As String
        Return Path.Combine(ctx.GameRoot, "system", "states.bin")
    End Function

    Private Function PreparedChanges(ctx As InstallContext, original As Byte(), vr As Boolean) As Dictionary(Of String, Byte())
        Dim result = If(vr, ExperimentalFiles.Changes(ctx).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value), New Dictionary(Of String, Byte()))
        result.Add("startup", StartupMovies.Patch(original))
        Return result
    End Function

    Private Sub EmulateDeployment(ctx As InstallContext, changes As IReadOnlyDictionary(Of String, Byte()))
        For Each pair In changes
            Dim target As String
            Select Case pair.Key
                Case "startup" : target = States(ctx)
                Case "graphics" : target = ctx.GraphicsPath
                Case "proxy" : target = Path.Combine(ctx.GameRoot, "d3d11.dll")
                Case "restrictions" : target = Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml")
                Case Else : Throw New Exception("Unexpected test target")
            End Select
            File.WriteAllBytes(target, pair.Value)
        Next
    End Sub

    Public Sub Run(root As String, original As Byte(), check As Action(Of Boolean, String))
        Dim patched = StartupMovies.Patch(original)
        Dim ctx = Context(root, "desktop", original), transaction As New OwnedFileTransaction(ctx)
        Dim desktop = PreparedChanges(ctx, original, False)
        transaction.Prepare(desktop, experimental:=False, apply:=False)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath), entry = journal.Entries.Single()
        check(journal.Schema = 3 AndAlso entry.Target = "startup" AndAlso entry.OriginalExists, "desktop startup uses a one-target schema-3 journal")
        check(File.ReadAllBytes(States(ctx)).SequenceEqual(original) AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(Graphics), "startup staging does not change game files")
        check(File.ReadAllBytes(Path.Combine(transaction.Folder, entry.Backup)).SequenceEqual(original) AndAlso File.ReadAllBytes(Path.Combine(transaction.Folder, entry.AppliedBackup)).SequenceEqual(patched), "startup staging saves exact original and approved applied bytes")
        check(transaction.Recover() AndAlso Not transaction.Pending AndAlso File.ReadAllBytes(States(ctx)).SequenceEqual(original), "interrupted startup staging restores safely before deployment")
        transaction.Prepare(desktop, apply:=False)
        EmulateDeployment(ctx, desktop)
        check(transaction.Recover() AndAlso File.ReadAllBytes(States(ctx)).SequenceEqual(original) AndAlso Not transaction.Recover(), "applied desktop startup restores exact bytes and recovery is idempotent")
        check(Not File.Exists(Path.Combine(ctx.GameRoot, "d3d11.dll")), "desktop startup transaction never installs a proxy")

        Dim combinedCtx = Context(root, "combined", original), combined As New OwnedFileTransaction(combinedCtx)
        Dim changes = PreparedChanges(combinedCtx, original, True)
        Dim restrictionsPath = Path.Combine(combinedCtx.GameRoot, "system", "hardware_settings_restrictions.xml")
        Dim restrictions = File.ReadAllBytes(restrictionsPath)
        combined.Prepare(changes, experimental:=True, apply:=False)
        journal = SafeFiles.ReadJson(Of RecoveryJournal)(combined.JournalPath)
        check(journal.Schema = 3 AndAlso journal.Entries.Count = 4 AndAlso journal.Entries.All(Function(item) item.AppliedBackup.Length > 0), "combined VR/startup stages all four fixed targets")
        File.WriteAllBytes(combinedCtx.GraphicsPath, changes("graphics"))
        File.WriteAllBytes(States(combinedCtx), changes("startup"))
        check(combined.Recover() AndAlso File.ReadAllBytes(combinedCtx.GraphicsPath).SequenceEqual(Graphics) AndAlso File.ReadAllBytes(States(combinedCtx)).SequenceEqual(original) AndAlso File.ReadAllBytes(restrictionsPath).SequenceEqual(restrictions), "partially deployed combined session restores every changed original")
        check(Not File.Exists(Path.Combine(combinedCtx.GameRoot, "d3d11.dll")), "interrupted combined deployment does not create an absent proxy")
        combined.Prepare(changes, experimental:=True, apply:=False)
        EmulateDeployment(combinedCtx, changes)
        Dim formatted = Encoding.UTF8.GetBytes(System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(changes("graphics"))).ToString())
        File.WriteAllBytes(combinedCtx.GraphicsPath, formatted)
        check(combined.Recover() AndAlso File.ReadAllBytes(combinedCtx.GraphicsPath).SequenceEqual(Graphics) AndAlso File.ReadAllBytes(States(combinedCtx)).SequenceEqual(original) AndAlso File.ReadAllBytes(restrictionsPath).SequenceEqual(restrictions) AndAlso Not File.Exists(Path.Combine(combinedCtx.GameRoot, "d3d11.dll")), "schema-3 keeps graphics-only formatting recovery and restores startup exactly")

        combined.Prepare(changes, experimental:=True, apply:=False)
        EmulateDeployment(combinedCtx, changes)
        Dim foreign = patched.Concat(New Byte() {0}).ToArray()
        File.WriteAllBytes(States(combinedCtx), foreign)
        Reject(Sub() combined.Recover(), check, "startup recovery refuses even a one-byte external edit")
        check(combined.Pending AndAlso File.ReadAllBytes(States(combinedCtx)).SequenceEqual(foreign) AndAlso File.ReadAllBytes(combinedCtx.GraphicsPath).SequenceEqual(Graphics) AndAlso File.ReadAllBytes(restrictionsPath).SequenceEqual(restrictions) AndAlso Not File.Exists(Path.Combine(combinedCtx.GameRoot, "d3d11.dll")),
              "a startup conflict leaves only the startup file and the journal; the other targets are restored")
        File.WriteAllBytes(States(combinedCtx), patched)
        check(combined.Recover(), "reviewed startup conflict can recover when approved bytes are restored")

        For Each damage In {"original", "applied", "forged-applied", "forged-original"}
            Dim damagedCtx = Context(root, damage, original), damaged As New OwnedFileTransaction(damagedCtx)
            damaged.Prepare(PreparedChanges(damagedCtx, original, False), apply:=False)
            File.WriteAllBytes(States(damagedCtx), patched)
            Dim receipt = SafeFiles.ReadJson(Of RecoveryJournal)(damaged.JournalPath), owned = receipt.Entries.Single()
            Dim backup = Path.Combine(damaged.Folder, If(damage.EndsWith("original", StringComparison.Ordinal), owned.Backup, owned.AppliedBackup))
            File.WriteAllBytes(backup, foreign)
            If damage = "forged-applied" Then owned.AppliedHash = SafeFiles.HashBytes(foreign)
            If damage = "forged-original" Then owned.OriginalHash = SafeFiles.HashBytes(foreign)
            If damage.StartsWith("forged", StringComparison.Ordinal) Then SafeFiles.SaveJson(damaged.JournalPath, receipt)
            Reject(Sub() damaged.Recover(), check, "startup recovery refuses " & damage & " backup damage")
            check(damaged.Pending AndAlso File.ReadAllBytes(States(damagedCtx)).SequenceEqual(patched), "damaged startup evidence preserves the current target and pending journal")
        Next

        For Each schema In {1, 2}
            Dim legacyCtx = Context(root, "forged-schema-" & schema, original), legacy As New OwnedFileTransaction(legacyCtx)
            legacy.Prepare(PreparedChanges(legacyCtx, original, False), apply:=False)
            journal = SafeFiles.ReadJson(Of RecoveryJournal)(legacy.JournalPath) : journal.Schema = schema
            SafeFiles.SaveJson(legacy.JournalPath, journal)
            Reject(Sub() legacy.Recover(), check, "legacy schema cannot claim the new startup target")
            check(File.ReadAllBytes(States(legacyCtx)).SequenceEqual(original), "forged legacy startup ownership preserves original file")
        Next
        For Each targets In {New String() {"graphics", "startup"}, New String() {"graphics", "proxy", "restrictions"}, New String() {"startup", "startup"}}
            Dim mixedCtx = Context(root, "mixed-" & Guid.NewGuid().ToString("N"), original), mixed As New OwnedFileTransaction(mixedCtx)
            mixed.Prepare(PreparedChanges(mixedCtx, original, True), experimental:=True, apply:=False)
            journal = SafeFiles.ReadJson(Of RecoveryJournal)(mixed.JournalPath)
            journal.Entries = targets.Select(Function(target) journal.Entries.Single(Function(item) item.Target = target)).ToList()
            SafeFiles.SaveJson(mixed.JournalPath, journal)
            Reject(Sub() mixed.Recover(), check, "schema-3 rejects incomplete or duplicate target sets")
            check(File.ReadAllBytes(States(mixedCtx)).SequenceEqual(original), "invalid target set is refused before any writes")
        Next
        Dim ownershipCtx = Context(root, "forged-ownership", original), ownership As New OwnedFileTransaction(ownershipCtx)
        ownership.Prepare(PreparedChanges(ownershipCtx, original, False), apply:=False)
        journal = SafeFiles.ReadJson(Of RecoveryJournal)(ownership.JournalPath) : journal.Entries.Single().OriginalExists = False
        SafeFiles.SaveJson(ownership.JournalPath, journal)
        Reject(Sub() ownership.Recover(), check, "startup must own an existing original and cannot claim deletion-only ownership")

        Dim invalidCtx = Context(root, "invalid-prepare", original), invalid As New OwnedFileTransaction(invalidCtx)
        Reject(Sub() invalid.Prepare(New Dictionary(Of String, Byte()) From {{"startup", patched}, {"graphics", Graphics}}, apply:=False), check, "desktop startup cannot mix an unrelated graphics write")
        Reject(Sub() invalid.Prepare(New Dictionary(Of String, Byte()) From {{"startup", patched}}, experimental:=True, apply:=False), check, "experimental startup cannot omit the three VR targets")
        Reject(Sub() invalid.Prepare(New Dictionary(Of String, Byte()) From {{"startup", foreign}}, apply:=False), check, "startup preparation validates the exact approved transform")
        check(Not invalid.Pending AndAlso File.ReadAllBytes(States(invalidCtx)).SequenceEqual(original), "invalid startup preparation writes no journal or game changes")
        File.Delete(States(invalidCtx))
        Reject(Sub() invalid.Prepare(New Dictionary(Of String, Byte()) From {{"startup", patched}}, apply:=False), check, "missing original startup file refuses preparation")
        check(Not invalid.Pending, "missing startup original creates no pending journal")

        Dim legacyRestrictionsCtx = Context(root, "legacy-restrictions", original), legacyRestrictions As New OwnedFileTransaction(legacyRestrictionsCtx)
        legacyRestrictions.Prepare(ExperimentalFiles.Changes(legacyRestrictionsCtx), experimental:=True, apply:=False)
        journal = SafeFiles.ReadJson(Of RecoveryJournal)(legacyRestrictions.JournalPath)
        journal.Schema = 1 : journal.Entries = journal.Entries.Where(Function(item) item.Target = "restrictions").ToList()
        SafeFiles.SaveJson(legacyRestrictions.JournalPath, journal)
        Reject(Sub() legacyRestrictions.Recover(), check, "schema-1 still refuses restrictions ownership")
        check(Not File.Exists(Path.Combine(ctx.GameRoot, "dirt3_game.exe")) AndAlso Not File.Exists(Path.Combine(combinedCtx.GameRoot, "dirt3_game.exe")), "startup recovery tests create no game executable or launch")
    End Sub
End Module

