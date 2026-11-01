Imports DiRT3VR
Imports System.IO
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports System.Xml.Linq

' #13ad25e5: a graphics file rewritten by DiRT 3 during VR must not leave the proxy and the patched
' game files behind, and the user decides what happens to graphics changes made during the session.
Module RecoveryConflictTests
    Private Const OriginalXml As String = "<?xml version=""1.0"" encoding=""UTF-8"" ?>" & vbCrLf &
        "<hardware_settings_config version=""56""><cpu><threadStrategy parallelUpdateRender=""true"" workerMapFile=""keep.xml""/></cpu><graphics_card><directx forcedx9=""false""/><stereo enabled=""false""/>" &
        "<resolution width=""1920"" height=""1080"" aspect=""auto"" fullscreen=""true"" vsync=""1"" multisampling=""4xmsaa""><refreshRate rate=""120""/></resolution><shadows enabled=""true"" size=""2048""/><gamma level=""1.0""/></graphics_card><audio mixing=""original""/></hardware_settings_config>"

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "recovery-conflict", name)
        Dim ctx As New InstallContext(game, Path.Combine(root, "recovery-conflict-users", name), Path.Combine(game, "graphics.xml"), Path.Combine(game, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system"))
        Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.WriteAllText(ctx.GraphicsPath, OriginalXml)
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Restrictions(ctx))
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        Return ctx
    End Function

    Private Function Proxy(ctx As InstallContext) As String
        Return Path.Combine(ctx.GameRoot, "d3d11.dll")
    End Function

    Private Function Restrictions(ctx As InstallContext) As String
        Return Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml")
    End Function

    ' A VR session as it is while DiRT 3 runs: journaled, then every target replaced.
    Private Function Deploy(ctx As InstallContext) As IReadOnlyDictionary(Of String, Byte())
        Dim changes = ExperimentalFiles.Changes(ctx, 1800, 1950)
        Call New OwnedFileTransaction(ctx).Prepare(changes, experimental:=True, apply:=False)
        File.WriteAllBytes(ctx.GraphicsPath, changes("graphics"))
        File.WriteAllBytes(Proxy(ctx), changes("proxy"))
        File.WriteAllBytes(Restrictions(ctx), changes("restrictions"))
        Return changes
    End Function

    Private Function Edit(data As Byte(), change As Action(Of XElement)) As Byte()
        Dim document = XDocument.Parse(Encoding.UTF8.GetString(data))
        change(document.Root)
        Return Encoding.UTF8.GetBytes(If(document.Declaration Is Nothing, "", document.Declaration.ToString()) & document.ToString(SaveOptions.DisableFormatting))
    End Function

    ' What DiRT 3 did on 31 Oct: 1280x720 at 100 Hz instead of the headset size; optionally a
    ' setting changed in its options menu during the session too.
    Private Function Rewritten(applied As Byte(), Optional userChange As Boolean = False) As Byte()
        Return Edit(applied, Sub(config)
                                 Dim resolution = config.Element("graphics_card").Element("resolution")
                                 resolution.SetAttributeValue("width", "1280") : resolution.SetAttributeValue("height", "720")
                                 resolution.Element("refreshRate").SetAttributeValue("rate", "100")
                                 config.Document.Declaration = New XDeclaration("1.0", "UTF-8", Nothing)
                                 If userChange Then config.Element("graphics_card").Element("shadows").SetAttributeValue("size", "1024")
                             End Sub)
    End Function

    Private Function Failure(action As Action) As Exception
        Try
            action()
        Catch ex As IOException
            Return ex
        End Try
        Return Nothing
    End Function

    Private Function Conflict(action As Action) As RecoveryConflictException
        Return TryCast(Failure(action), RecoveryConflictException)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim originalBytes = Encoding.UTF8.GetBytes(OriginalXml)
        Dim restrictionsOriginal = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"))
        Dim userChanged = Encoding.UTF8.GetBytes(OriginalXml.Replace("size=""2048""", "size=""1024"""))

        ' What counts as DiRT 3 rewriting only the session's own values.
        Dim applied = ExperimentalFiles.Graphics(originalBytes, 1800, 1950)
        check(ExperimentalFiles.OnlySessionValuesChanged(Rewritten(applied), applied), "DiRT 3's resolution and refresh rewrite counts as session values only")
        check(ExperimentalFiles.OnlySessionValuesChanged(Edit(applied, Sub(config)
                                                                        Dim resolution = config.Element("graphics_card").Element("resolution")
                                                                        resolution.SetAttributeValue("aspect", "16:9") : resolution.Element("refreshRate").Remove()
                                                                        config.Element("cpu").Element("threadStrategy").SetAttributeValue("parallelUpdateRender", "true")
                                                                    End Sub), applied), "aspect, a dropped refresh rate and the serial-render switch are session values too")
        check(Not ExperimentalFiles.OnlySessionValuesChanged(Rewritten(applied, userChange:=True), applied), "a changed shadow setting is not a session value")
        check(Not ExperimentalFiles.OnlySessionValuesChanged(Edit(applied, Sub(config) config.Element("graphics_card").Add(New XElement("mirrors", New XAttribute("enabled", "false")))), applied), "an added setting is not a session value")
        check(Not ExperimentalFiles.OnlySessionValuesChanged(Encoding.UTF8.GetBytes("external edit"), applied) AndAlso Not ExperimentalFiles.OnlySessionValuesChanged(Encoding.UTF8.GetBytes("<hardware_settings_config/>"), applied), "invalid or foreign XML is not a rewrite")
        Dim merged = ExperimentalFiles.RestoreSessionValues(Rewritten(applied, userChange:=True), originalBytes)
        check(merged IsNot Nothing AndAlso ExperimentalFiles.SameXml(merged, userChanged), "keeping DiRT 3's changes puts back every session value and keeps the shadow change")
        check(Encoding.UTF8.GetString(merged).StartsWith("<?xml", StringComparison.Ordinal), "the game's XML declaration is kept")
        Dim noRate = ExperimentalFiles.RestoreSessionValues(Edit(applied, Sub(config) config.Element("graphics_card").Element("resolution").Element("refreshRate").Remove()), originalBytes)
        check(noRate IsNot Nothing AndAlso ExperimentalFiles.SameXml(noRate, originalBytes), "a refresh rate DiRT 3 dropped comes back")
        check(ExperimentalFiles.RestoreSessionValues(Encoding.UTF8.GetBytes("external edit"), originalBytes) Is Nothing AndAlso
              ExperimentalFiles.RestoreSessionValues(Encoding.UTF8.GetBytes("<hardware_settings_config><cpu/></hardware_settings_config>"), originalBytes) Is Nothing, "a file that isn't DiRT 3's settings XML can't be merged")

        ' The 31 Oct case: only the known rewrite. Everything is restored without asking.
        Dim ctx = Context(root, "rewrite")
        Dim changes = Deploy(ctx)
        File.WriteAllBytes(ctx.GraphicsPath, Rewritten(changes("graphics")))
        Dim transaction As New OwnedFileTransaction(ctx)
        check(transaction.GraphicsConflict() = GraphicsConflictKind.None, "the known rewrite needs no decision")
        check(transaction.Recover() AndAlso Not transaction.Pending, "DiRT 3's resolution/refresh rewrite no longer blocks recovery")
        check(File.ReadAllText(ctx.GraphicsPath) = OriginalXml AndAlso Not File.Exists(Proxy(ctx)) AndAlso File.ReadAllBytes(Restrictions(ctx)).SequenceEqual(restrictionsOriginal), "every original is restored exactly after the rewrite")
        check(Directory.GetFiles(transaction.Folder, "game-written-*.xml").Length = 1, "DiRT 3's rewritten file is kept for inspection")

        ' A setting changed during the session as well: the rest is restored, graphics waits for a choice.
        ctx = Context(root, "user-change") : changes = Deploy(ctx) : transaction = New OwnedFileTransaction(ctx)
        Dim changed = Rewritten(changes("graphics"), userChange:=True)
        File.WriteAllBytes(ctx.GraphicsPath, changed)
        Dim reported = Conflict(Sub() transaction.Recover())
        check(reported IsNot Nothing AndAlso reported.Targets.SequenceEqual({"graphics"}) AndAlso reported.Message.Contains("other game files were restored") AndAlso reported.Message.Contains("Restore original files"), "a graphics conflict is reported on its own, saying the rest was restored")
        check(Not File.Exists(Proxy(ctx)) AndAlso File.ReadAllBytes(Restrictions(ctx)).SequenceEqual(restrictionsOriginal) AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(changed) AndAlso transaction.Pending,
              "the proxy and restrictions are restored; the changed graphics file stays and recovery stays pending")
        check(transaction.GraphicsConflict() = GraphicsConflictKind.Mergeable, "the user can choose for a mergeable graphics conflict")
        check(Conflict(Sub() transaction.Recover()) IsNot Nothing AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(changed), "trying again without a choice changes nothing")
        check(transaction.Recover(GraphicsRecoveryChoice.KeepGameChanges) AndAlso Not transaction.Pending, "keeping DiRT 3's changes completes recovery")
        check(ExperimentalFiles.SameXml(File.ReadAllBytes(ctx.GraphicsPath), userChanged), "display values come from before VR and the shadow change stays")
        check(Directory.GetFiles(transaction.Folder, "game-written-*.xml").Length = 1, "the changed file is kept as a copy")

        ctx = Context(root, "restore-original") : changes = Deploy(ctx) : transaction = New OwnedFileTransaction(ctx)
        File.WriteAllBytes(ctx.GraphicsPath, Rewritten(changes("graphics"), userChange:=True))
        check(transaction.Recover(GraphicsRecoveryChoice.RestoreOriginal) AndAlso File.ReadAllText(ctx.GraphicsPath) = OriginalXml AndAlso Not File.Exists(Proxy(ctx)) AndAlso Not transaction.Pending,
              "choosing the settings from before the session restores the exact original and everything else in one go")

        ' The real files from 31 Oct (#13ad25e5), byte for byte: DiRT 3 re-added the XML declaration and
        ' changed the headset's 3072x3264 at 120 Hz to 1280x720 at 100 Hz.
        Dim evidence = Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31")
        Dim realOriginal = File.ReadAllBytes(Path.Combine(evidence, "original.xml")), realApplied = File.ReadAllBytes(Path.Combine(evidence, "applied.xml")), realCurrent = File.ReadAllBytes(Path.Combine(evidence, "current.xml"))
        check(SafeFiles.HashBytes(realOriginal) = "bcbbc06ab1bb83592125bc27e86c827fd091612f47bd7c2592ed9cfcf5ddb40a" AndAlso SafeFiles.HashBytes(realApplied) = "de8c1bc7314ed3b9ee6a2e655baa5c9fb34e24983e607fa121c08040e6781a74" AndAlso
              SafeFiles.HashBytes(realCurrent) = "7bf3d547962f79eef192757cc20efde205a963a45dce3fd92557ef7a179dca4a", "the 31 Oct evidence matches the hashes in that session's journal")
        check(ExperimentalFiles.SameXml(ExperimentalFiles.Graphics(realOriginal, 3072, 3264), realApplied), "today's VR transform reproduces the file applied on 31 Oct")
        check(ExperimentalFiles.OnlySessionValuesChanged(realCurrent, realApplied), "the real 31 Oct rewrite counts as DiRT 3 changing only session values")
        check(ExperimentalFiles.SameXml(ExperimentalFiles.RestoreSessionValues(realCurrent, realOriginal), realOriginal), "keeping DiRT 3's changes from 31 Oct gives back the original settings")
        ctx = Context(root, "real-rewrite")
        File.WriteAllBytes(ctx.GraphicsPath, realOriginal)
        changes = ExperimentalFiles.Changes(ctx, 3072, 3264)
        Call New OwnedFileTransaction(ctx).Prepare(changes, experimental:=True, apply:=False)
        File.WriteAllBytes(Proxy(ctx), changes("proxy")) : File.WriteAllBytes(Restrictions(ctx), changes("restrictions"))
        File.WriteAllBytes(ctx.GraphicsPath, realCurrent)
        transaction = New OwnedFileTransaction(ctx)
        check(transaction.Recover() AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(realOriginal) AndAlso Not File.Exists(Proxy(ctx)) AndAlso File.ReadAllBytes(Restrictions(ctx)).SequenceEqual(restrictionsOriginal) AndAlso Not transaction.Pending,
              "the 31 Oct session now restores every original exactly, without asking")

        ' Not settings XML any more: only the original can come back.
        ctx = Context(root, "invalid") : changes = Deploy(ctx) : transaction = New OwnedFileTransaction(ctx)
        File.WriteAllText(ctx.GraphicsPath, "external edit")
        reported = Conflict(Sub() transaction.Recover())
        check(reported IsNot Nothing AndAlso Not File.Exists(Proxy(ctx)) AndAlso File.ReadAllText(ctx.GraphicsPath) = "external edit", "invalid graphics XML stays as it is; the proxy is still removed")
        check(transaction.GraphicsConflict() = GraphicsConflictKind.NotMergeable, "invalid XML offers only the original")
        reported = Conflict(Sub() transaction.Recover(GraphicsRecoveryChoice.KeepGameChanges))
        check(reported IsNot Nothing AndAlso reported.Message.Contains("no longer valid") AndAlso File.ReadAllText(ctx.GraphicsPath) = "external edit" AndAlso transaction.Pending, "invalid XML can't be kept")
        check(transaction.Recover(GraphicsRecoveryChoice.RestoreOriginal) AndAlso File.ReadAllText(ctx.GraphicsPath) = OriginalXml, "invalid XML can be replaced by the original")

        ' Conflicts in other files stay as they are; no graphics choice applies to them.
        ctx = Context(root, "restrictions") : changes = Deploy(ctx) : transaction = New OwnedFileTransaction(ctx)
        Dim foreign = changes("restrictions").Concat(New Byte() {32}).ToArray()
        File.WriteAllBytes(Restrictions(ctx), foreign)
        reported = Conflict(Sub() transaction.Recover(GraphicsRecoveryChoice.RestoreOriginal))
        check(reported IsNot Nothing AndAlso reported.Targets.SequenceEqual({"restrictions"}) AndAlso File.ReadAllBytes(Restrictions(ctx)).SequenceEqual(foreign), "a changed restrictions file stays as it is, whatever the graphics choice")
        check(File.ReadAllText(ctx.GraphicsPath) = OriginalXml AndAlso Not File.Exists(Proxy(ctx)) AndAlso transaction.Pending, "graphics and proxy are restored despite the restrictions conflict")
        check(transaction.GraphicsConflict() = GraphicsConflictKind.None, "no graphics decision is offered for another file's conflict")
        File.WriteAllBytes(Restrictions(ctx), restrictionsOriginal) ' For example after Steam verified the game files.
        check(transaction.Recover() AndAlso Not transaction.Pending, "once the file is back, recovery completes")

        ctx = Context(root, "proxy") : changes = Deploy(ctx) : transaction = New OwnedFileTransaction(ctx)
        File.WriteAllText(Proxy(ctx), "another mod")
        reported = Conflict(Sub() transaction.Recover())
        check(reported IsNot Nothing AndAlso reported.Targets.SequenceEqual({"proxy"}) AndAlso File.ReadAllText(Proxy(ctx)) = "another mod" AndAlso File.ReadAllText(ctx.GraphicsPath) = OriginalXml AndAlso File.ReadAllBytes(Restrictions(ctx)).SequenceEqual(restrictionsOriginal),
              "another mod's d3d11.dll is kept; the other files are restored")

        ' Damaged recovery evidence still stops everything before any file is touched.
        ctx = Context(root, "damaged") : changes = Deploy(ctx) : transaction = New OwnedFileTransaction(ctx)
        File.WriteAllBytes(ctx.GraphicsPath, Rewritten(changes("graphics"), userChange:=True))
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        File.AppendAllText(Path.Combine(transaction.Folder, journal.Entries.Single(Function(entry) entry.Target = "restrictions").Backup), " ")
        Dim damaged = Failure(Sub() transaction.Recover(GraphicsRecoveryChoice.RestoreOriginal))
        check(damaged IsNot Nothing AndAlso TypeOf damaged IsNot RecoveryConflictException AndAlso File.Exists(Proxy(ctx)) AndAlso File.ReadAllBytes(Restrictions(ctx)).SequenceEqual(changes("restrictions")) AndAlso transaction.Pending,
              "a damaged backup still stops recovery before any file is touched")

        ' The elevated worker gets the same choice and its reason reaches the launcher; every outcome is logged.
        check(RecoveryWorker.ParseChoice("ask") = GraphicsRecoveryChoice.Ask AndAlso RecoveryWorker.ParseChoice("keep") = GraphicsRecoveryChoice.KeepGameChanges AndAlso RecoveryWorker.ParseChoice("original") = GraphicsRecoveryChoice.RestoreOriginal AndAlso
              RecoveryWorker.ChoiceArgument(GraphicsRecoveryChoice.RestoreOriginal) = "original" AndAlso RecoveryWorker.ChoiceArgument(GraphicsRecoveryChoice.KeepGameChanges) = "keep", "the elevated worker gets the same graphics choice")
        check(Failure(Sub() RecoveryWorker.ParseChoice("delete")) IsNot Nothing, "an unknown worker choice is refused")
        SafeFiles.SaveJson(Path.Combine(ctx.UserRoot, "logs", "recovery-worker.json"), New With {.updatedUtc = DateTime.UtcNow, .message = "worker said why", .errorType = "test"})
        check(RecoveryWorker.WorkerFailure(ctx, DateTime.UtcNow.AddSeconds(-1)) = "worker said why", "the elevated worker's reason is shown instead of a generic failure")
        check(RecoveryWorker.WorkerFailure(ctx, DateTime.UtcNow.AddMinutes(5)) Is Nothing, "an older worker report is not reused")

        ctx = Context(root, "log") : changes = Deploy(ctx)
        File.WriteAllBytes(ctx.GraphicsPath, Rewritten(changes("graphics"), userChange:=True))
        Dim logPath = Path.Combine(ctx.UserRoot, "logs", "recovery.json")
        Dim asyncFailure As Exception = Nothing
        Try
            Task.Run(Function() RecoveryWorker.RecoverAsync(ctx)).GetAwaiter().GetResult()
        Catch ex As IOException
            asyncFailure = ex
        End Try
        check(TypeOf asyncFailure Is RecoveryConflictException AndAlso File.ReadAllText(logPath).Contains("""pending""") AndAlso File.ReadAllText(logPath).Contains("beyond DiRT3VR"), "a refused restore is written to logs\recovery.json")
        check(Task.Run(Function() RecoveryWorker.RecoverAsync(ctx, GraphicsRecoveryChoice.KeepGameChanges)).GetAwaiter().GetResult() AndAlso File.ReadAllText(logPath).Contains("""restored""") AndAlso File.ReadAllText(logPath).Contains("""keep"""),
              "the completed restore and the choice are logged")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Dim changes = Deploy(ctx)
        File.WriteAllBytes(ctx.GraphicsPath, Rewritten(changes("graphics"), userChange:=True))
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        ' Without Application.Run, Windows Forms falls back to a plain SynchronizationContext between
        ' message pumps, so the async Restore handler resumed on the thread pool and finished after the
        ' form was disposed (a crash on Windows; not seen under Wine). The launcher itself always runs
        ' under Application.Run, so each click gets the same Windows Forms context here.
        Dim previousContext = Threading.SynchronizationContext.Current
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim asked As New List(Of GraphicsConflictKind)
            Dim answer = GraphicsRecoveryChoice.Ask
            form.GraphicsChoicePrompt = Function(kind)
                                            asked.Add(kind)
                                            Return answer
                                        End Function
            Dim restore = DirectCast(form.Controls.Find("RestoreFiles", True).Single(), Button)
            Dim notice = DirectCast(form.Controls.Find("Notice", True).Single(), Label)
            Dim clickAndWait = Function() As Boolean
                                   notice.Text = "waiting"
                                   Threading.SynchronizationContext.SetSynchronizationContext(New WindowsFormsSynchronizationContext())
                                   restore.PerformClick()
                                   Dim deadline = System.Diagnostics.Stopwatch.StartNew()
                                   While (notice.Text = "waiting" OrElse Not restore.Enabled) AndAlso deadline.Elapsed < TimeSpan.FromSeconds(20)
                                       Application.DoEvents() : Threading.Thread.Sleep(20)
                                   End While
                                   Return notice.Text <> "waiting"
                               End Function
            check(clickAndWait() AndAlso asked.SequenceEqual({GraphicsConflictKind.Mergeable}) AndAlso notice.Text.Contains("beyond DiRT3VR") AndAlso New OwnedFileTransaction(ctx).Pending,
                  "Restore original files asks what to keep; Decide later leaves graphics pending (" & notice.Text & ")")
            check(Not File.Exists(Proxy(ctx)), "the other files were restored before asking")
            answer = GraphicsRecoveryChoice.KeepGameChanges
            check(clickAndWait() AndAlso asked.Count = 2 AndAlso notice.Text.StartsWith("Original files restored. DiRT 3's graphics settings keep", StringComparison.Ordinal) AndAlso Not New OwnedFileTransaction(ctx).Pending,
                  "keeping DiRT 3's changes finishes recovery from the launcher (" & notice.Text & ")")
            form.Close()
        End Using
        Threading.SynchronizationContext.SetSynchronizationContext(previousContext)
    End Sub
End Module
