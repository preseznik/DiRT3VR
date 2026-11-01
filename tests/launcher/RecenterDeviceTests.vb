Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module RecenterDeviceTests
    Private Const WheelId As String = "{11111111-1111-1111-1111-111111111111}", WheelProduct As String = "{22222222-2222-2222-2222-222222222222}"

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Pressed(Optional button As Integer = -1, Optional pov As UInteger = UInteger.MaxValue, Optional connected As Boolean = True) As DrivingInput.Sample
        Dim sample = DrivingInput.Sample.Empty()
        sample.Connected = If(connected, 1UI, 0UI)
        If button >= 0 Then sample.Buttons(button) = 1
        sample.Pov(0) = pov
        Return sample
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim wheel As New DrivingInput.Device(0, WheelId, "Test wheel", WheelProduct)
        Dim pad As New DrivingInput.Device(1, "xinput:0", "Xbox controller 1", "xinput_pad")
        Dim rest = Pressed()

        Dim found = RecenterDeviceBinding.Detect(wheel, rest, Pressed(button:=17))
        check(found IsNot Nothing AndAlso found.Input = "button:17" AndAlso found.Describe() = "Button 17 on Test wheel", "a fresh wheel button press is bound")
        check(RecenterDeviceBinding.Detect(wheel, Pressed(button:=17), Pressed(button:=17)) Is Nothing, "a button already held is not a new press")
        Dim hat = RecenterDeviceBinding.Detect(wheel, rest, Pressed(pov:=27000))
        check(hat IsNot Nothing AndAlso hat.Input = "hat:0:left" AndAlso hat.Describe() = "Hat 0 left on Test wheel", "a hat direction is bound")
        check(RecenterDeviceBinding.Detect(wheel, rest, Pressed(pov:=4500)) Is Nothing, "a diagonal is not bound, so one gesture can't mean two directions")
        Dim xbox = RecenterDeviceBinding.Detect(pad, rest, Pressed(button:=13))
        check(xbox IsNot Nothing AndAlso xbox.Describe() = "B on Xbox controller 1", "Xbox buttons are bound by name")
        check(RecenterDeviceBinding.Detect(pad, rest, Pressed(button:=10)) Is Nothing AndAlso RecenterDeviceBinding.Detect(wheel, rest, Pressed(button:=3, connected:=False)) Is Nothing, "unused Xbox bits and disconnected devices are ignored")
        For Each value In {0UI, 35999UI, 9000UI, 18000UI, 27000UI, 4500UI, UInteger.MaxValue}
            check(RecenterDeviceBinding.PovDirection(value) = If(value = 0UI OrElse value = 35999UI, 0, If(value = 9000UI, 1, If(value = 18000UI, 2, If(value = 27000UI, 3, -1)))), "hat value " & value & " maps to its direction")
        Next

        For Each bad In {New RecenterDeviceBinding With {.DeviceId = "wheel", .Product = WheelProduct, .Input = "button:1"},
                         New RecenterDeviceBinding With {.DeviceId = WheelId, .Product = WheelProduct, .Input = "button:128"},
                         New RecenterDeviceBinding With {.DeviceId = WheelId, .Product = WheelProduct, .Input = "axis:0"},
                         New RecenterDeviceBinding With {.DeviceId = WheelId, .Product = WheelProduct, .Input = "hat:4:up"},
                         New RecenterDeviceBinding With {.DeviceId = "xinput:0", .Product = "xinput_pad", .Input = "button:10"},
                         New RecenterDeviceBinding With {.DeviceId = "xinput:0", .Product = "xinput_pad", .Input = "hat:0:up"},
                         New RecenterDeviceBinding With {.DeviceId = "xinput:4", .Product = "xinput_pad", .Input = "button:12"}}
            check(Refused(Sub() bad.Validate()), "invalid recenter binding refused: " & bad.DeviceId & " " & bad.Input)
        Next
        found.Validate() : hat.Validate() : xbox.Validate()

        ' Edge: only after a release seen while DiRT 3 is in front and the device connected.
        Dim edge As New RecenterEdge()
        check(Not edge.Update(True, True, True), "a button held when watching starts does nothing")
        check(Not edge.Update(True, True, False) AndAlso edge.Update(True, True, True) AndAlso Not edge.Update(True, True, True), "a press after a release recenters once")
        check(Not edge.Update(True, True, False) AndAlso Not edge.Update(True, False, True) AndAlso Not edge.Update(True, True, True), "a button held while switching to DiRT 3 does nothing")
        check(Not edge.Update(True, True, False) AndAlso Not edge.Update(False, True, False) AndAlso Not edge.Update(True, True, True), "reconnecting a device needs a fresh release")

        ' Watcher with a scripted device and focus.
        Dim folder = Path.Combine(root, "recenter-watch")
        Directory.CreateDirectory(folder)
        Dim request = Path.Combine(folder, "recenter.request")
        Dim script As New Queue(Of (Boolean, Boolean))({(False, True), (True, True), (True, True), (False, True), (True, False), (False, True), (True, True)})
        Dim current = (False, True)
        Using watcher As New RecenterDeviceWatcher(found, request, Function() If(current.Item1, Pressed(button:=17), Pressed()), Function() current.Item2)
            Dim results As New List(Of Boolean)
            While script.Count > 0
                current = script.Dequeue()
                results.Add(watcher.Tick())
                If results.Last() AndAlso results.Count = 2 Then
                    check(File.Exists(request), "a press creates the renderer's request file")
                    ' The renderer has not consumed it yet: the next press must not fail or duplicate.
                End If
            End While
            check(results.SequenceEqual({False, True, False, False, False, False, False}) AndAlso watcher.Requests = 1, "only a fresh press with DiRT 3 in front asks, and an unconsumed request isn't duplicated (" & String.Join(",", results) & ")")
            File.Delete(request)
            current = (False, True) : watcher.Tick() : current = (True, True)
            check(watcher.Tick() AndAlso watcher.Requests = 2, "after the renderer takes the request, the next press asks again")
        End Using
        check(File.Exists(Path.Combine(folder, "recenter-controller.json")) AndAlso File.ReadAllText(Path.Combine(folder, "recenter-controller.json")).Contains("""requests"": 2"), "the session logs record the controller recenter requests")
        check(Refused(Sub() Call New RecenterDeviceWatcher(found, "relative.request", Function() rest, Function() True).Dispose()), "the request path must be absolute")

        ' Settings keep the binding; profiles never carry it but keep the local one when loaded.
        Dim ctx = Context(root, "recenter-device-settings")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterDevice = found}.Save(ctx)
        Dim loaded = LauncherSettings.Load(ctx)
        check(loaded.RecenterDevice IsNot Nothing AndAlso loaded.RecenterDevice.Describe() = "Button 17 on Test wheel", "the controller binding round trips through launcher settings")
        Dim profile = ProfileValues.Capture(loaded)
        Dim exported = Text.Json.JsonSerializer.Serialize(profile)
        check(Not exported.Contains(WheelId) AndAlso Not exported.Contains("RecenterDevice"), "settings profiles never carry machine-specific device identities")
        check(profile.ApplyTo(loaded).RecenterDevice IsNot Nothing, "loading a profile keeps this PC's controller binding")
        SafeFiles.SaveJson(ctx.PreferencesPath, New With {.Version = 1, .RecenterDevice = New With {.DeviceId = "nope", .Product = "x", .Input = "button:1", .DeviceName = "x"}})
        check(Refused(Sub() LauncherSettings.Load(ctx)), "an edited, invalid controller binding is refused")

        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Dim game = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterDevice = found}, headset)
        check(game.Environment("DIRT3VR_RECENTER_REQUEST_FILE") = Path.Combine(logs, "recenter.request"), "with a bound button, the renderer is told where controller recenter requests appear")
        Environment.SetEnvironmentVariable("DIRT3VR_RECENTER_REQUEST_FILE", "C:\stale\recenter.request")
        Try
            Dim unbound = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}, headset)
            check(Not unbound.Environment.ContainsKey("DIRT3VR_RECENTER_REQUEST_FILE"), "without a bound button the renderer gets no request path (so it never polls for the file), even if one was inherited")
        Finally
            Environment.SetEnvironmentVariable("DIRT3VR_RECENTER_REQUEST_FILE", Nothing)
        End Try
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "recenter-device-ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Dim binding As New RecenterDeviceBinding With {.DeviceId = WheelId, .DeviceName = "Test wheel", .Product = WheelProduct, .Input = "hat:0:up"}
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim button = DirectCast(form.Controls.Find("RecenterDevice", True).Single(), Button)
            Dim clear = DirectCast(form.Controls.Find("ClearRecenterDevice", True).Single(), Button)
            check(button.Text = "Bind a button…" AndAlso Not clear.Enabled AndAlso Not form.HasUnsavedSettings(), "no controller recenter by default")
            form.SetRecenterDevice(binding)
            check(button.Text = "Hat 0 up on Test wheel" AndAlso clear.Enabled AndAlso form.HasUnsavedSettings() AndAlso form.CurrentSettings().RecenterDevice IsNot Nothing, "a bound button shows and is an unsaved change")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            check(LauncherSettings.Load(ctx).RecenterDevice?.Input = "hat:0:up", "Save settings keeps the controller binding")
            form.SetRecenterDevice(Nothing)
            check(button.Text = "Bind a button…" AndAlso Not clear.Enabled AndAlso form.HasUnsavedSettings(), "Clear removes the binding")
            form.Close()
        End Using
        For Each candidate In {Path.Combine(AppContext.BaseDirectory, "driving_input.dll"), Path.Combine(root, "no-driving-input.dll")}
            Using dialog As New RecenterDeviceForm(candidate)
                dialog.ShowInTaskbar = False : dialog.StartPosition = FormStartPosition.Manual : dialog.Location = New Drawing.Point(-30000, -30000)
                dialog.Show() : Application.DoEvents()
                Dim expected = If(File.Exists(candidate), "No controllers detected", "Controller reading is not installed")
                check(dialog.StatusText.StartsWith(expected, StringComparison.Ordinal) OrElse dialog.StatusText.StartsWith("Waiting for a button", StringComparison.Ordinal), "the button dialog explains what it is waiting for (" & dialog.StatusText & ")")
                check(dialog.Binding Is Nothing, "nothing is bound without a press")
                dialog.Close()
            End Using
        Next
    End Sub
End Module
