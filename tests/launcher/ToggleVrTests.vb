Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

' Toggle VR (#288ceec1): the key and controller button settings, the environment the renderer reads (Lens's
' contract: DIRT3VR_TOGGLE_KEY "<vk> <modifiers>", DIRT3VR_TOGGLE_REQUEST_FILE only with a bound button),
' the button's watcher and its own log, and the Controls UI.
Module ToggleVrTests
    Private Const WheelId As String = "{11111111-1111-1111-1111-111111111111}", WheelProduct As String = "{22222222-2222-2222-2222-222222222222}"

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function WheelButton(input As String) As RecenterDeviceBinding
        Return New RecenterDeviceBinding With {.DeviceId = WheelId, .DeviceName = "Test wheel", .Product = WheelProduct, .Input = input}
    End Function

    Private Function Settings(Optional key As Integer = LauncherSettings.DefaultToggleVrKey, Optional modifiers As Integer = 0, Optional device As RecenterDeviceBinding = Nothing) As LauncherSettings
        Return New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .ToggleVrKey = key, .ToggleVrModifiers = modifiers, .ToggleVrDevice = device}
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' Settings: F6 by default, never the recenter or seat menu key; its own controller button.
        Dim defaults = Settings()
        check(defaults.ToggleVrKey = 117 AndAlso defaults.ToggleVrDevice Is Nothing AndAlso Not Refused(Sub() defaults.Validate()), "F6 toggles VR by default, with no controller button")
        check(Refused(Sub() Settings(121).Validate()) AndAlso Refused(Sub() Settings(9).Validate()) AndAlso Not Refused(Sub() Settings(121, 1).Validate()) AndAlso
              Refused(Sub() Settings(9, 2).Validate()), "the recenter (F10) and seat menu (Tab) keys and Alt+Tab are refused; Ctrl+F10 is fine")
        Dim cleared = Settings(0, 4)
        cleared.Validate()
        check(cleared.ToggleVrModifiers = 0, "no key keeps no modifiers")
        Dim withRecenter = Settings(device:=WheelButton("button:3"))
        withRecenter.RecenterDevice = WheelButton("button:3")
        Dim withSeat = Settings(device:=WheelButton("button:4"))
        withSeat.SeatMenuButton = WheelButton("button:4")
        check(Not Refused(Sub() Settings(device:=WheelButton("hat:0:left")).Validate()) AndAlso Refused(Sub() withRecenter.Validate()) AndAlso Refused(Sub() withSeat.Validate()) AndAlso
              Refused(Sub() Settings(device:=WheelButton("button:999")).Validate()), "its own button is kept; the recenter or seat menu button, or a damaged one, is refused")
        check(LauncherSettings.ToggleVrKeyProblem(117, 0, 121, 0, 117, 0) <> "" AndAlso LauncherSettings.ToggleVrKeyProblem(0, 0, 121, 0, 122, 0) = "", "the seat menu key is refused; no key is fine")

        ' The session environment.
        Dim ctx = Context(root, "toggle-vr")
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Dim plain = InteractiveSession.StartInfo(ctx, runtime, logs, defaults, headset)
        check(plain.Environment("DIRT3VR_TOGGLE_KEY") = "117 0" AndAlso Not plain.Environment.ContainsKey("DIRT3VR_TOGGLE_REQUEST_FILE"), "F6 is passed; without a button there is no request file")
        Dim bound = InteractiveSession.StartInfo(ctx, runtime, logs, Settings(117, 5, WheelButton("button:7")), headset)
        check(bound.Environment("DIRT3VR_TOGGLE_KEY") = "117 5" AndAlso bound.Environment("DIRT3VR_TOGGLE_REQUEST_FILE") = Path.Combine(logs, "toggle-vr.request"), "Ctrl+Shift+F6 and the button's request file are passed")
        Environment.SetEnvironmentVariable("DIRT3VR_TOGGLE_KEY", "117 0")
        Environment.SetEnvironmentVariable("DIRT3VR_TOGGLE_REQUEST_FILE", "C:\stale\toggle-vr.request")
        Try
            Dim none = InteractiveSession.StartInfo(ctx, runtime, logs, Settings(0), headset)
            check(Not none.Environment.ContainsKey("DIRT3VR_TOGGLE_KEY") AndAlso Not none.Environment.ContainsKey("DIRT3VR_TOGGLE_REQUEST_FILE"), "with no key and no button nothing is passed, even if inherited")
        Finally
            Environment.SetEnvironmentVariable("DIRT3VR_TOGGLE_KEY", Nothing)
            Environment.SetEnvironmentVariable("DIRT3VR_TOGGLE_REQUEST_FILE", Nothing)
        End Try

        ' The button's watcher: the recenter button's handshake with its own request file and log.
        Dim folder = Path.Combine(root, "toggle-vr-watch")
        Directory.CreateDirectory(folder)
        Dim request = Path.Combine(folder, "toggle-vr.request")
        Dim pressed = False
        Dim read = Function() As DrivingInput.Sample
                       Dim sample = DrivingInput.Sample.Empty()
                       sample.Connected = 1UI
                       If pressed Then sample.Buttons(7) = 1
                       Return sample
                   End Function
        Dim watcher As New RecenterDeviceWatcher(WheelButton("button:7"), request, read, Function() True, logName:="toggle-vr-controller.json")
        watcher.Tick() : pressed = True
        check(watcher.Tick() AndAlso File.Exists(request), "a press writes the toggle request")
        watcher.Dispose()
        check(File.Exists(Path.Combine(folder, "toggle-vr-controller.json")) AndAlso Not File.Exists(Path.Combine(folder, "recenter-controller.json")) AndAlso
              File.ReadAllText(Path.Combine(folder, "toggle-vr-controller.json")).Contains("""requests"": 1"), "the session logs record it in its own file, not the recenter one")

        ' Profiles keep this PC's key and button, like the seat menu's.
        Dim local = Settings(118, 1, WheelButton("button:7"))
        Dim applied = ProfileValues.Capture(Settings()).ApplyTo(local)
        check(applied.ToggleVrKey = 118 AndAlso applied.ToggleVrModifiers = 1 AndAlso applied.ToggleVrDevice IsNot Nothing, "loading a settings profile keeps this PC's Toggle VR key and button")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "toggle-vr-ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            Dim key = DirectCast(form.Controls.Find("ToggleVrKey", True).Single(), Button)
            Dim noKey = DirectCast(form.Controls.Find("ClearToggleVrKey", True).Single(), Button)
            Dim device = DirectCast(form.Controls.Find("ToggleVrDevice", True).Single(), Button)
            Dim notice = form.Controls.Find("Notice", True).Single()
            tabs.SelectedTab = tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Contains(key)) : Application.DoEvents()
            check(key.Text = "F6" AndAlso noKey.Enabled AndAlso device.Text = "Bind a button…" AndAlso Not form.HasUnsavedSettings(), "Controls shows F6 and no button by default")
            check(Not form.CaptureToggleKey(Keys.F5), "keys pass through until the Toggle VR key is clicked")
            key.PerformClick()
            check(key.Text = "Press a key…" AndAlso form.CaptureToggleKey(Keys.F10) AndAlso notice.Text.Contains("recentering need different keys") AndAlso
                  form.CaptureToggleKey(Keys.Tab) AndAlso notice.Text.Contains("seat menu need different keys") AndAlso key.Text = "Press a key…", "the recenter and seat menu keys are refused and capture continues")
            check(form.CaptureToggleKey(Keys.F5 Or Keys.Alt) AndAlso key.Text = "Alt+F5" AndAlso form.CurrentSettings().ToggleVrKey = 116 AndAlso form.CurrentSettings().ToggleVrModifiers = 2, "a key combination is captured")
            DirectCast(form.Controls.Find("RecenterKey", True).Single(), Button).PerformClick()
            check(form.CaptureRecenterKey(Keys.F5 Or Keys.Alt) AndAlso notice.Text.Contains("toggles VR") AndAlso form.CaptureRecenterKey(Keys.Escape), "the recenter key can't take Toggle VR's key")
            form.SetRecenterDevice(WheelButton("button:2"))
            form.SetToggleDevice(WheelButton("button:2"))
            check(device.Text = "Bind a button…" AndAlso notice.Text.Contains("recenters the view"), "the recenter button is refused")
            form.SetToggleDevice(WheelButton("hat:1:up"))
            check(device.Text = "Hat 1 up on Test wheel" AndAlso form.CurrentSettings().ToggleVrDevice?.Input = "hat:1:up", "a hat direction is bound")
            form.SetSeatMenuDevice(WheelButton("hat:1:up"))
            check(form.CurrentSettings().SeatMenuButton Is Nothing AndAlso notice.Text.Contains("toggles VR"), "the seat menu can't take Toggle VR's button")
            noKey.PerformClick()
            check(key.Text = "None" AndAlso form.CurrentSettings().ToggleVrKey = 0 AndAlso notice.Text.Contains("controller button still works"), "No key removes the key; the button still works")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.ToggleVrKey = 0 AndAlso saved.ToggleVrDevice?.Input = "hat:1:up" AndAlso saved.RecenterDevice?.Input = "button:2" AndAlso Not form.HasUnsavedSettings(), "Save settings keeps them")
            form.Close()
        End Using
    End Sub
End Module
