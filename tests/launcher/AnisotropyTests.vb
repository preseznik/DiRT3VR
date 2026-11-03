Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

' Anisotropic filtering (#15714dd2, launcher side; Wrench's renderer side): Graphics → Anisotropic filtering for
' Desktop and VR, DIRT3VR_ANISOTROPY = 1 (off) / 2 / 4 / 8 / 16 only for a choice other than the game's, and on the
' monitor a Launch session that installs the renderer for it (journal schema 10).
Module AnisotropyTests
    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "anisotropy", name, "game")
        Dim documents = Path.Combine(root, "anisotropy", name, "documents", "hardwaresettings")
        Directory.CreateDirectory(game) : Directory.CreateDirectory(documents)
        Dim ctx As New InstallContext(game, Path.Combine(root, "anisotropy", name, "users"), Path.Combine(documents, "hardware_settings_config.xml"), Path.Combine(root, "anisotropy", name, "package"))
        Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        Return ctx
    End Function

    Private Function Settings(Optional desktop As Integer = 0, Optional vr As Integer = 0) As LauncherSettings
        Return New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .DesktopAnisotropy = desktop, .VrAnisotropy = vr}
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' The settings: the game's own by default, only DiRT2VR's levels.
        Dim defaults = Settings()
        check(defaults.DesktopAnisotropy = 0 AndAlso defaults.VrAnisotropy = 0, "both start at the game's own filtering")
        defaults.Validate()
        check(Refused(Sub() Settings(desktop:=3).Validate()) AndAlso Refused(Sub() Settings(vr:=32).Validate()) AndAlso Refused(Sub() Settings(vr:=-1).Validate()), "other levels are refused")
        check(Anisotropy.Describe(0) = "Use game setting" AndAlso Anisotropy.Describe(1) = "Off" AndAlso Anisotropy.Describe(16) = "16×", "the choices read Use game setting, Off, 2× to 16×")
        Dim applied = ProfileValues.Capture(defaults).ApplyTo(Settings(16, 8))
        check(applied.DesktopAnisotropy = 16 AndAlso applied.VrAnisotropy = 8, "loading a settings profile keeps this PC's choices")
        Dim restored = LauncherDefaults.Graphics(Settings(16, 8))
        check(restored.DesktopAnisotropy = 0 AndAlso restored.VrAnisotropy = 0, "Restore graphics defaults puts both back to the game's")

        ' VR: only a choice other than the game's is passed, and only the VR one.
        Dim ctx = Context(root, "vr")
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Environment.SetEnvironmentVariable(Anisotropy.VariableName, "4")
        Try
            check(Not InteractiveSession.StartInfo(ctx, runtime, logs, Settings(desktop:=16), headset).Environment.ContainsKey(Anisotropy.VariableName), "VR on the game's filtering passes nothing, even if inherited or set for the desktop")
        Finally
            Environment.SetEnvironmentVariable(Anisotropy.VariableName, Nothing)
        End Try
        check(InteractiveSession.StartInfo(ctx, runtime, logs, Settings(vr:=8), headset).Environment(Anisotropy.VariableName) = "8", "VR 8× passes 8")
        check(InteractiveSession.StartInfo(ctx, runtime, logs, Settings(vr:=1), headset).Environment(Anisotropy.VariableName) = "1", "Off passes 1 (trilinear)")

        ' The monitor: a choice other than the game's makes Launch install the renderer for the session.
        check(Not DesktopStartupSession.NeedsSession(Settings(vr:=16)) AndAlso Not DesktopStartupSession.NeedsRenderer(Settings(vr:=16)), "a VR choice alone leaves Launch plain")
        check(DesktopStartupSession.NeedsSession(Settings(desktop:=4)) AndAlso DesktopStartupSession.NeedsRenderer(Settings(desktop:=1)), "a desktop choice, Off too, needs the renderer")
        Dim desktop = Context(root, "desktop")
        Dim changes = DesktopStartupSession.Changes(desktop, Settings(desktop:=16))
        check(changes.Keys.SequenceEqual({"proxy"}), "the session changes only the renderer (" & String.Join(",", changes.Keys) & ")")
        Dim transaction As New OwnedFileTransaction(desktop)
        transaction.Prepare(changes, apply:=False, monitorRenderer:=True)
        check(SafeFiles.ReadJson(Of RecoveryJournal)(transaction.JournalPath).Schema = 10, "it is a schema 10 journal, like the monitor seat menu's renderer")
        File.WriteAllText(Path.Combine(desktop.GameRoot, "d3d11.dll"), "owned test payload")
        check(transaction.Recover() AndAlso Not File.Exists(Path.Combine(desktop.GameRoot, "d3d11.dll")), "and the renderer is removed after the session")
        Dim desktopLogs = Path.Combine(desktop.UserRoot, "logs", "session-fixture")
        Dim start = DesktopStartupSession.StartInfo(desktop, Settings(desktop:=16), desktopLogs)
        check(start.Environment(Anisotropy.VariableName) = "16" AndAlso start.Environment("DIRT3VR_DIAGNOSTICS") = "1" AndAlso start.Environment("DIRT3VR_LOG_DIR") = desktopLogs AndAlso
              Not start.Environment.ContainsKey(SeatChannel.EnvironmentName) AndAlso Not start.Environment.ContainsKey("DIRT3VR_DIRECT_PRACTICE") AndAlso start.ArgumentList.Count = 0,
              "Launch passes 16, the renderer's switch and log folder, and starts DiRT 3 as usual")
        Environment.SetEnvironmentVariable(Anisotropy.VariableName, "4")
        Try
            Dim plain = DesktopStartupSession.StartInfo(desktop, Settings(), desktopLogs)
            check(Not plain.Environment.ContainsKey(Anisotropy.VariableName) AndAlso Not plain.Environment.ContainsKey("DIRT3VR_LOG_DIR"), "on the game's filtering nothing is passed, even if inherited")
        Finally
            Environment.SetEnvironmentVariable(Anisotropy.VariableName, Nothing)
        End Try
        Dim direct = Settings(desktop:=2)
        direct.LaunchMode = "direct"
        check(DesktopStartupSession.StartInfo(desktop, direct, desktopLogs).Environment(Anisotropy.VariableName) = "2", "direct practice on the monitor passes it too")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call Settings().Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim desktopBox = DirectCast(form.Controls.Find("DesktopAnisotropy", True).Single(), ComboBox)
            Dim vrBox = DirectCast(form.Controls.Find("VrAnisotropy", True).Single(), ComboBox)
            check(desktopBox.Items.Count = 6 AndAlso vrBox.Items.Count = 6 AndAlso desktopBox.SelectedIndex = 0 AndAlso vrBox.SelectedIndex = 0 AndAlso Not form.HasUnsavedSettings(), "Desktop and VR start on Use game setting")
            desktopBox.SelectedIndex = 4 : vrBox.SelectedIndex = 5 : Application.DoEvents()
            check(form.CurrentSettings().DesktopAnisotropy = 8 AndAlso form.CurrentSettings().VrAnisotropy = 16 AndAlso form.HasUnsavedSettings(), "8× on the monitor and 16× in VR are launcher settings")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.DesktopAnisotropy = 8 AndAlso saved.VrAnisotropy = 16 AndAlso Not form.HasUnsavedSettings(), "Save settings keeps them")
            form.Close()
        End Using
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(DirectCast(form.Controls.Find("DesktopAnisotropy", True).Single(), ComboBox).SelectedIndex = 4 AndAlso DirectCast(form.Controls.Find("VrAnisotropy", True).Single(), ComboBox).SelectedIndex = 5,
                  "the launcher opens with the saved choices")
            form.Close()
        End Using
    End Sub
End Module
