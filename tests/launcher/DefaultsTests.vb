Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module DefaultsTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function Customized() As LauncherSettings
        Return New LauncherSettings With {
            .ModernInterface = False, .RuntimeMode = "steamvr", .SteamRuntimeManifest = "C:\SteamVR\steamxr_win32.json", .VdxrRuntimeManifest = "C:\VDXR\virtualdesktop-openxr-32.json",
            .DiagnosticLogging = True, .ExperimentalVrEnabled = True, .VrRenderScale = 150, .DesktopWidth = 1920, .DesktopHeight = 1080,
            .DesktopDisplayMode = "fullscreen", .DesktopVSync = "on", .SkipStartupMovies = True, .RecenterKey = 120, .RecenterModifiers = 1,
            .RecenterDevice = New RecenterDeviceBinding With {.DeviceId = "{11111111-1111-1111-1111-111111111111}", .DeviceName = "Test wheel", .Product = "{22222222-2222-2222-2222-222222222222}", .Input = "button:3"}}
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim custom = Customized()
        Dim defaults As New LauncherSettings()
        Dim graphics = LauncherDefaults.Graphics(custom)
        check(graphics.DesktopWidth = 0 AndAlso graphics.DesktopHeight = 0 AndAlso graphics.DesktopDisplayMode = "game" AndAlso graphics.DesktopVSync = "game" AndAlso graphics.VrRenderScale = 100,
              "graphics defaults use the game's own desktop settings and 100% VR render scale")
        check(Not graphics.ModernInterface AndAlso graphics.RuntimeMode = "steamvr" AndAlso graphics.DiagnosticLogging AndAlso graphics.SkipStartupMovies AndAlso graphics.RecenterKey = 120 AndAlso graphics.RecenterModifiers = 1 AndAlso
              graphics.RecenterDevice IsNot Nothing AndAlso graphics.ExperimentalVrEnabled AndAlso graphics.SteamRuntimeManifest = custom.SteamRuntimeManifest, "graphics defaults leave every other choice alone")
        check(custom.VrRenderScale = 150 AndAlso graphics.RecenterDevice IsNot custom.RecenterDevice, "the current settings object is not changed")
        graphics.Validate()

        Dim all = LauncherDefaults.All(custom)
        check(all.ModernInterface = defaults.ModernInterface AndAlso all.RuntimeMode = "vdxr" AndAlso Not all.DiagnosticLogging AndAlso Not all.SkipStartupMovies AndAlso all.VrRenderScale = 100 AndAlso
              all.DesktopDisplayMode = "game" AndAlso all.DesktopVSync = "game" AndAlso all.DesktopWidth = 0 AndAlso all.RecenterKey = RecenterShortcut.DefaultKey AndAlso all.RecenterModifiers = 0 AndAlso all.RecenterDevice Is Nothing,
              "launcher defaults reset appearance, runtime choice, logging, startup, graphics, recenter key and controller button")
        check(all.SteamRuntimeManifest = custom.SteamRuntimeManifest AndAlso all.VdxrRuntimeManifest = custom.VdxrRuntimeManifest AndAlso all.ExperimentalVrEnabled, "launcher defaults keep runtime locations and the experimental VR opt-in")
        all.Validate()
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "defaults-ui")
        Dim custom = Customized()
        custom.SteamRuntimeManifest = "" : custom.VdxrRuntimeManifest = ""
        custom.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim notice = DirectCast(form.Controls.Find("Notice", True).Single(), Label)
            Dim questions As New List(Of String)
            Dim answer = False
            form.ConfirmPrompt = Function(caption, body)
                                     questions.Add(caption & ": " & body)
                                     Return answer
                                 End Function
            check(form.Controls.Find("GraphicsDefaults", True).Length = 1 AndAlso form.Controls.Find("LauncherDefaults", True).Length = 1, "both restore defaults buttons exist")
            Dim before = form.CurrentSettings()
            check(Not form.RestoreDefaults(graphicsOnly:=True) AndAlso questions.Count = 1 AndAlso questions(0).StartsWith("Restore graphics defaults", StringComparison.Ordinal), "restoring defaults asks first")
            check(Text.Json.JsonSerializer.Serialize(form.CurrentSettings()) = Text.Json.JsonSerializer.Serialize(before) AndAlso Not form.HasUnsavedSettings(), "Cancel changes nothing")
            answer = True
            check(form.RestoreDefaults(graphicsOnly:=True), "OK restores graphics defaults")
            Dim afterGraphics = form.CurrentSettings()
            check(afterGraphics.VrRenderScale = 100 AndAlso afterGraphics.DesktopDisplayMode = "game" AndAlso afterGraphics.DesktopVSync = "game" AndAlso afterGraphics.DesktopWidth = 0 AndAlso
                  afterGraphics.SkipStartupMovies AndAlso afterGraphics.RecenterKey = 120 AndAlso afterGraphics.RecenterDevice IsNot Nothing, "the graphics controls show their defaults; other tabs keep their values")
            check(form.HasUnsavedSettings() AndAlso LauncherSettings.Load(ctx).VrRenderScale = 150 AndAlso notice.Text.Contains("Save settings"), "nothing is saved until Save settings or Launch")
            check(Not form.RestoreDefaults(graphicsOnly:=True) AndAlso questions.Count = 2 AndAlso notice.Text.Contains("already"), "already at the graphics defaults: no question, just a note")
            check(form.RestoreDefaults(graphicsOnly:=False) AndAlso questions.Count = 3 AndAlso questions(2).Contains("unsaved changes"), "launcher defaults warn that unsaved changes are replaced")
            Dim afterAll = form.CurrentSettings()
            check(afterAll.ModernInterface AndAlso afterAll.RuntimeMode = "vdxr" AndAlso Not afterAll.DiagnosticLogging AndAlso Not afterAll.SkipStartupMovies AndAlso afterAll.RecenterKey = RecenterShortcut.DefaultKey AndAlso
                  afterAll.RecenterDevice Is Nothing AndAlso afterAll.ExperimentalVrEnabled, "launcher defaults reach every tab and keep the experimental VR opt-in")
            check(LauncherSettings.Load(ctx).DiagnosticLogging, "launcher defaults are not saved either")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            check(Not LauncherSettings.Load(ctx).DiagnosticLogging AndAlso LauncherSettings.Load(ctx).VrRenderScale = 100 AndAlso Not form.HasUnsavedSettings(), "Save settings keeps the restored defaults")
            form.Close()
        End Using
    End Sub
End Module
