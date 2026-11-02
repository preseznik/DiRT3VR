Imports DiRT3VR
Imports System.IO

Module MotionBlurTests
    Private Function Fixture(name As String) As Byte()
        Return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "effects", name))
    End Function

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    ' A game folder with VR's files, the synthetic effects file and one car's camera file.
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "blur", name, "game")
        Dim documents = Path.Combine(root, "blur", name, "documents", "hardwaresettings")
        Directory.CreateDirectory(game) : Directory.CreateDirectory(documents)
        Dim ctx As New InstallContext(game, Path.Combine(root, "blur", name, "users"), Path.Combine(documents, "hardware_settings_config.xml"), Path.Combine(root, "blur", name, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system")) : Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload")) : Directory.CreateDirectory(Path.Combine(game, "postprocess"))
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"), True)
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31", "original.xml"), ctx.GraphicsPath, True)
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        File.WriteAllBytes(MotionBlur.EffectsPath(ctx), Fixture("effects.bin"))
        Directory.CreateDirectory(Path.GetDirectoryName(SeatPositions.CameraPath(ctx, "frx")))
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "seat", "cameras.bin"), SeatPositions.CameraPath(ctx, "frx"), True)
        Return ctx
    End Function

    Private Function Deploy(ctx As InstallContext, changes As IDictionary(Of String, Byte())) As Boolean
        For Each pair In changes
            Dim target = If(pair.Key = MotionBlur.Target, MotionBlur.EffectsPath(ctx), If(SeatPositions.IsTarget(pair.Key), SeatPositions.CameraPath(ctx, pair.Key.Substring(5)),
                         If(pair.Key = "proxy", Path.Combine(ctx.GameRoot, "d3d11.dll"), If(pair.Key = "graphics", ctx.GraphicsPath, Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml")))))
            File.WriteAllBytes(target, pair.Value)
        Next
        Return True
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' The edit matches Lens's tools/motion_blur.py byte for byte (fixtures made by it).
        Dim original = Fixture("effects.bin")
        check(MotionBlur.Read(original).SequenceEqual({6.0, 0.25, 7.0289}), "the motion blur lengths are read in file order (race_base 6 and 0.25, FlashbackFX_01 7.0289)")
        Dim disabled = MotionBlur.Disable(original)
        check(disabled.SequenceEqual(Fixture("effects-no-blur.bin")), "switching motion blur off gives exactly the reference tool's file")
        check(MotionBlur.Read(disabled).All(Function(length) length = 0) AndAlso MotionBlur.Disable(disabled).SequenceEqual(disabled), "every length is zero afterwards, and doing it again changes nothing")
        check(Refused(Sub() MotionBlur.Disable(Fixture("effects-without-motion-blur.bin"))) AndAlso
              Refused(Sub() MotionBlur.Disable(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "seat", "cameras.bin")))) AndAlso
              Refused(Sub() MotionBlur.Disable(Text.Encoding.UTF8.GetBytes("<PostProcessEffects/>"))), "files without motion blur lengths, other binary XML and plain XML are refused")

        ' A VR session switches it off; a missing or unusable file leaves it on with a note.
        Dim ctx = Context(root, "session")
        Dim note As String = Nothing
        check(MotionBlur.SessionChange(ctx, note).SequenceEqual(disabled) AndAlso note = "", "a VR session writes the effects file with motion blur off")
        File.WriteAllBytes(MotionBlur.EffectsPath(ctx), disabled)
        check(MotionBlur.SessionChange(ctx, note) Is Nothing AndAlso note = "", "an effects file that already has no motion blur is left alone")
        File.WriteAllText(MotionBlur.EffectsPath(ctx), "not binary xml")
        check(MotionBlur.SessionChange(ctx, note) Is Nothing AndAlso note.StartsWith("Motion blur stays on", StringComparison.Ordinal), "an unusable effects file keeps motion blur on and says so")
        File.Delete(MotionBlur.EffectsPath(ctx))
        check(MotionBlur.SessionChange(ctx, note) Is Nothing AndAlso note <> "", "so does a missing one")
        File.WriteAllBytes(MotionBlur.EffectsPath(ctx), original)

        ' Journal: schema 7, the effects file is restored, and only the exact edit is accepted.
        Dim changes = ExperimentalFiles.Changes(ctx, 1800, 1950).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        changes.Add(MotionBlur.Target, disabled)
        Dim transaction As New OwnedFileTransaction(ctx)
        Dim wrong = New Dictionary(Of String, Byte())(changes)
        wrong(MotionBlur.Target) = Fixture("effects-without-motion-blur.bin")
        check(Refused(Sub() transaction.Prepare(wrong, experimental:=True, apply:=False)) AndAlso Not transaction.Pending, "an effects file that isn't the motion blur edit of the original is refused before anything is staged")
        check(Refused(Sub() transaction.Prepare(New Dictionary(Of String, Byte()) From {{MotionBlur.Target, disabled}}, apply:=False)) AndAlso Not transaction.Pending, "motion blur changes belong to a VR session")
        transaction.Prepare(changes, experimental:=True, apply:=False)
        check(SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath).Schema = 7 AndAlso SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath).Seats Is Nothing, "a VR session with the motion blur change writes a schema 7 journal")
        Deploy(ctx, changes)
        Dim journalText = File.ReadAllText(transaction.JournalPath)
        File.WriteAllText(transaction.JournalPath, System.Text.RegularExpressions.Regex.Replace(journalText, """Schema"":\s*7", """Schema"": 6"))
        check(Refused(Sub() transaction.Recover()) AndAlso File.ReadAllBytes(MotionBlur.EffectsPath(ctx)).SequenceEqual(disabled), "a schema 6 journal can't carry the effects file; nothing is touched")
        File.WriteAllText(transaction.JournalPath, journalText)
        check(transaction.Recover() AndAlso File.ReadAllBytes(MotionBlur.EffectsPath(ctx)).SequenceEqual(original), "recovery restores the original effects file")

        ' With seat positions too: both in one schema 7 journal.
        Dim settings As New LauncherSettings With {.SeatOffsets = New Dictionary(Of String, SeatOffset) From {{"frx", New SeatOffset With {.Up = 2}}}}
        Dim seats = SeatPositions.Changes(ctx, settings)
        changes = ExperimentalFiles.Changes(ctx, 1800, 1950).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        changes.Add(MotionBlur.Target, disabled)
        For Each pair In seats.Files
            changes.Add(pair.Key, pair.Value)
        Next
        transaction.Prepare(changes, experimental:=True, apply:=False, seats:=seats.Offsets)
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath)
        check(journal.Schema = 7 AndAlso journal.Seats.Keys.SequenceEqual({"frx"}) AndAlso journal.Entries.Any(Function(entry) entry.Target = MotionBlur.Target), "seat positions and the motion blur change share one schema 7 journal")
        Deploy(ctx, changes)
        check(transaction.Recover() AndAlso File.ReadAllBytes(MotionBlur.EffectsPath(ctx)).SequenceEqual(original) AndAlso
              File.ReadAllBytes(SeatPositions.CameraPath(ctx, "frx")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "seat", "cameras.bin"))), "recovery restores both")
    End Sub
End Module
