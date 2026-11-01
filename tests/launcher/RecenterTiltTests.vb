Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module RecenterTiltTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "recenter-tilt", name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "recenter-tilt-users", name), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        check(New LauncherSettings().RecenterKeepLevel, "recentering keeps the horizon level by default")
        Dim ctx = Context(root, "settings")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterKeepLevel = False}.Save(ctx)
        Dim loaded = LauncherSettings.Load(ctx)
        check(Not loaded.RecenterKeepLevel, "turning it off is saved with the launcher settings")
        SafeFiles.SaveJson(ctx.PreferencesPath, New With {.Version = 1})
        check(LauncherSettings.Load(ctx).RecenterKeepLevel, "settings saved before this option keep the horizon level")
        check(Not ProfileValues.Capture(loaded).ApplyTo(loaded).RecenterKeepLevel AndAlso ProfileValues.Capture(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).ApplyTo(loaded).RecenterKeepLevel = False,
              "loading a settings profile keeps this PC's horizon choice")
        check(LauncherDefaults.All(loaded).RecenterKeepLevel, "Restore launcher defaults turns the level horizon back on")

        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Dim level = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}, headset)
        Dim tilt = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterKeepLevel = False}, headset)
        check(level.Environment("DIRT3VR_RECENTER_TILT") = "0" AndAlso tilt.Environment("DIRT3VR_RECENTER_TILT") = "1", "the renderer is told whether recentering also resets pitch and roll")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim box = DirectCast(form.Controls.Find("RecenterKeepLevel", True).Single(), CheckBox)
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            check(box.Checked AndAlso tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Text = "Controls").Contains(box), "Controls has the horizon option, on by default")
            box.Checked = False
            check(Not form.CurrentSettings().RecenterKeepLevel AndAlso form.HasUnsavedSettings(), "unticking it is an unsaved change")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            check(Not LauncherSettings.Load(ctx).RecenterKeepLevel, "Save settings keeps it")
            form.Close()
        End Using
    End Sub
End Module
