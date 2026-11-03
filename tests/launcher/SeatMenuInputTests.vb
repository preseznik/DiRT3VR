Imports DiRT3VR
Imports System.IO
Imports System.IO.MemoryMappedFiles
Imports System.Windows.Forms

' Controllers in the in-game seat menu (#c137c681): what each device gives the menu, which device it
' listens to, presses held across an open or close, the channel fields, and the settings and UI.
Module SeatMenuInputTests
    Private Const WheelId As String = "{11111111-1111-1111-1111-111111111111}", WheelProduct As String = "{22222222-2222-2222-2222-222222222222}"
    Private ReadOnly Pad As New DrivingInput.Device(0UI, "xinput:2", "Xbox controller 3", "xinput_pad")
    Private ReadOnly Wheel As New DrivingInput.Device(1UI, WheelId, "Test wheel", WheelProduct)

    Private Function Bit(index As Integer) As UInteger
        Return 1UI << index
    End Function

    Private Function Held(Optional buttons As Integer() = Nothing, Optional pov As UInteger = UInteger.MaxValue) As DrivingInput.Sample
        Dim sample = DrivingInput.Sample.Empty()
        sample.Connected = 1UI
        For Each button In If(buttons, Array.Empty(Of Integer)())
            sample.Buttons(button) = 1
        Next
        sample.Pov(0) = pov
        Return sample
    End Function

    Private Function PadButton(input As String) As RecenterDeviceBinding
        Return New RecenterDeviceBinding With {.DeviceId = Pad.Id, .DeviceName = Pad.Name, .Product = Pad.Product, .Input = input}
    End Function

    Private Function WheelButton(input As String) As RecenterDeviceBinding
        Return New RecenterDeviceBinding With {.DeviceId = WheelId, .DeviceName = Wheel.Name, .Product = WheelProduct, .Input = input}
    End Function

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim y = PadButton("button:15"), boundWheel = WheelButton("button:40")

        ' One device: closed, only the bound button counts; open, the menu's own buttons and hat do too.
        Dim closed = SeatMenuInput.Read(Pad, Held({0, 12}), Nothing, False)
        check(closed.Held = 0UI AndAlso closed.Buttons.All(Function(mask) mask = 0UI) AndAlso Not closed.Hat, "closed, a pad's D-pad and A stay the game's")
        Dim opening = SeatMenuInput.Read(Pad, Held({15}), y, False)
        check(opening.Held = Bit(SeatMenuInput.OpenBit) AndAlso opening.Buttons(0) = Bit(15), "the bound Y opens the menu (bit 13) and is the menu's button")
        Dim moving = SeatMenuInput.Read(Pad, Held({0, 12, 14}), y, True)
        check(moving.Held = (Bit(SeatMenuInput.UpBit) Or Bit(SeatMenuInput.SaveBit) Or Bit(SeatMenuInput.SidewaysBit)) AndAlso moving.Buttons(0) = (&H700FUI Or Bit(15)) AndAlso Not moving.Hat,
              "open: D-pad up, A and X are up, save and sideways; the D-pad, A, B, X and Y are kept from the game (" & moving.Buttons(0).ToString("X") & ")")
        check(SeatMenuInput.Read(Pad, Held({1, 2, 3, 13}), Nothing, True).Held = (Bit(SeatMenuInput.DownBit) Or Bit(SeatMenuInput.LeftBit) Or Bit(SeatMenuInput.RightBit) Or Bit(SeatMenuInput.CancelBit)),
              "D-pad down, left and right and B are down, left, right and cancel")
        Dim hat = SeatMenuInput.Read(Wheel, Held(pov:=9000UI), boundWheel, True)
        check(hat.Held = Bit(SeatMenuInput.RightBit) AndAlso hat.Hat AndAlso hat.Buttons(1) = Bit(8), "a wheel's first hat moves the seat; its bound button 40 is kept from the game (mask 1, bit 8)")
        check(SeatMenuInput.Read(Wheel, Held(pov:=4500UI), Nothing, True).Held = 0UI AndAlso SeatMenuInput.Read(Wheel, Held(pov:=0UI), Nothing, True).Held = Bit(SeatMenuInput.UpBit),
              "a diagonal moves nothing; hat up is up")
        check(SeatMenuInput.Read(Wheel, Held({40}), boundWheel, False).Held = Bit(SeatMenuInput.OpenBit) AndAlso SeatMenuInput.Read(Pad, Held({40}), boundWheel, False).Held = 0UI,
              "the bound button counts only on its own device")
        check(SeatMenuInput.Read(Wheel, Held(pov:=0UI), WheelButton("hat:1:left"), False).Hat, "a bound hat direction keeps the hat from the game")
        check(SeatMenuInput.Read(Pad, DrivingInput.Sample.Empty(), y, True).Held = 0UI, "a disconnected pad gives nothing")

        ' Buttons the menu needs can't open it.
        check(SeatMenuInput.Reserved(PadButton("button:12")) <> "" AndAlso SeatMenuInput.Reserved(PadButton("button:0")) <> "" AndAlso SeatMenuInput.Reserved(y) = "", "a pad's A or D-pad can't open the menu; Y can")
        check(SeatMenuInput.Reserved(WheelButton("hat:0:up")) <> "" AndAlso SeatMenuInput.Reserved(WheelButton("hat:1:up")) = "" AndAlso SeatMenuInput.Reserved(WheelButton("button:12")) = "",
              "a wheel's first hat can't open it; another hat or any button can")

        ' Which device the menu listens to, and its hint.
        Dim idle = SeatMenuInput.Read(Pad, Held(), Nothing, True), turning = SeatMenuInput.Read(Wheel, Held(pov:=27000UI), Nothing, True)
        check(SeatMenuInput.Choose({(Pad, idle), (Wheel, turning)}, Nothing).Device Is Wheel, "a device in use wins")
        check(SeatMenuInput.Choose({(Pad, idle), (Wheel, SeatMenuInput.Read(Wheel, Held(), boundWheel, True))}, boundWheel).Device Is Wheel, "else the bound button's device")
        check(SeatMenuInput.Choose({(Wheel, SeatMenuInput.Read(Wheel, Held(), Nothing, True)), (Pad, idle)}, Nothing).Device Is Pad, "else an Xbox pad, so its buttons are kept from the game before the first press")
        check(SeatMenuInput.Choose({(Wheel, SeatMenuInput.Read(Wheel, Held(), Nothing, True))}, Nothing).Device Is Nothing, "else none")
        check(SeatMenuInput.Hint(Nothing, Nothing) = SeatMenuInput.XboxHint AndAlso SeatMenuInput.Hint(Pad, y) = SeatMenuInput.XboxHint, "keyboard and pad hint")
        check(SeatMenuInput.Hint(Wheel, boundWheel) = "Arrows / hat: move. Hold Shift: sideways. Enter / Button 40: save. Esc: cancel." AndAlso Not SeatMenuInput.Hint(Wheel, Nothing).Contains("Button"),
              "a wheel's hint names its hat and its bound button")

        ' From poll to poll: the press that opens the menu doesn't also save it.
        Dim tracker As New SeatMenuTracker()
        Dim poll = Function(menuOpen As Boolean, focused As Boolean, padButtons As Integer(), wheelPov As UInteger)
                       Return tracker.Update({(Pad, SeatMenuInput.Read(Pad, Held(padButtons), y, menuOpen)), (Wheel, SeatMenuInput.Read(Wheel, Held(pov:=wheelPov), y, menuOpen))}, menuOpen, focused, y)
                   End Function
        check(poll(False, True, {15}, UInteger.MaxValue).Input.Held = 0UI, "a button already held when DiRT 3 comes to the front waits for its release")
        check(poll(False, True, {}, UInteger.MaxValue).Device Is Pad AndAlso poll(False, True, {15}, UInteger.MaxValue).Input.Held = Bit(SeatMenuInput.OpenBit), "released and pressed again, Y opens the menu")
        check(poll(True, True, {15, 12}, UInteger.MaxValue).Input.Held = 0UI, "as the menu opens, Y and A still held count for nothing")
        check(poll(True, True, {12}, UInteger.MaxValue).Input.Held = 0UI, "A held since the menu opened still waits")
        Dim used = poll(True, True, {0}, UInteger.MaxValue)
        check(used.Device Is Pad AndAlso used.Input.Held = Bit(SeatMenuInput.UpBit) AndAlso used.Input.Buttons(0) <> 0UI, "a fresh D-pad press moves the seat")
        check(poll(True, True, {}, 0UI).Device Is Pad, "while the menu is open, the pad that moved it keeps it")
        check(poll(True, False, {0}, UInteger.MaxValue).Device Is Nothing, "nothing while DiRT 3 is not in front")
        check(poll(True, True, {}, UInteger.MaxValue).Input.Held = 0UI, "back in front, nothing held")
        check(poll(False, True, {}, UInteger.MaxValue).Input.Held = 0UI AndAlso poll(True, True, {}, UInteger.MaxValue).Input.Held = 0UI AndAlso poll(True, True, {}, 0UI).Device Is Wheel,
              "after the menu closes, the next device used takes it")

        ' The channel the renderer reads.
        Dim name = "Local\DiRT3VR.Seat.Test" & Guid.NewGuid().ToString("N")
        Using channel As New SeatChannel(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}, Nothing, name), opened = MemoryMappedFile.OpenExisting(name), renderer = opened.CreateViewAccessor()
            SeatMenuInput.Publish(channel, (Wheel, hat))
            Dim id(15) As Byte
            renderer.ReadArray(44, id, 0, 16)
            check(renderer.ReadUInt32(16) Mod 2UI = 0UI AndAlso renderer.ReadUInt32(20) = Bit(SeatMenuInput.RightBit) AndAlso renderer.ReadUInt32(36) = 2UI AndAlso New Guid(id) = Guid.Parse(WheelId) AndAlso
                  renderer.ReadUInt32(64) = Bit(8) AndAlso renderer.ReadUInt32(76) = 1UI, "a wheel is published with its GUID, held bits, blocked button and hat")
            SeatMenuInput.Publish(channel, (Pad, moving))
            check(renderer.ReadUInt32(36) = 1UI AndAlso renderer.ReadUInt32(40) = 2UI AndAlso renderer.ReadUInt32(60) = (&H700FUI Or Bit(15)) AndAlso renderer.ReadUInt32(76) = 0UI, "a pad is published with its XInput slot and wButtons mask")
            SeatMenuInput.Publish(channel, (Nothing, SeatMenuInput.Contribution.None()))
            check(renderer.ReadUInt32(20) = 0UI AndAlso renderer.ReadUInt32(36) = 0UI AndAlso renderer.ReadUInt32(60) = 0UI, "no device: nothing held, nothing kept from the game")
        End Using

        ' Settings: the menu's own buttons and the recenter button are refused; profiles keep this PC's button.
        Dim settings = Function(button As RecenterDeviceBinding, recenter As RecenterDeviceBinding) New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .SeatMenuButton = button, .RecenterDevice = recenter}
        check(Not Refused(Sub() settings(y, Nothing).Validate()) AndAlso Refused(Sub() settings(PadButton("button:13"), Nothing).Validate()) AndAlso Refused(Sub() settings(WheelButton("hat:0:down"), Nothing).Validate()),
              "settings keep Y and refuse B or the first hat")
        check(Refused(Sub() settings(y, PadButton("button:15")).Validate()) AndAlso Not Refused(Sub() settings(y, PadButton("button:9")).Validate()), "the seat menu and recentering need different buttons")
        check(Refused(Sub() settings(PadButton("button:99"), Nothing).Validate()), "a damaged binding is refused")
        check(LauncherSettings.SeatMenuKeyProblem(122, 0, 121, 0) = "" AndAlso LauncherSettings.SeatMenuKeyProblem(0, 0, 121, 0) = "" AndAlso LauncherSettings.SeatMenuKeyProblem(38, 0, 121, 0) <> "" AndAlso
              LauncherSettings.SeatMenuKeyProblem(13, 0, 121, 0) <> "" AndAlso LauncherSettings.SeatMenuKeyProblem(121, 0, 121, 0) <> "" AndAlso LauncherSettings.SeatMenuKeyProblem(121, 1, 121, 0) = "",
              "the menu key: F11 or none; not an arrow, Enter or the recenter key")
        Dim local = settings(y, Nothing)
        Dim profile = ProfileValues.Capture(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""})
        check(profile.ApplyTo(local).SeatMenuButton Is y, "a settings profile keeps this PC's seat menu button")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "seat-menu-ui")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "seat-menu-ui-users"), Path.Combine(game, "graphics.xml"), root)
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            Dim key = DirectCast(form.Controls.Find("SeatMenuKey", True).Single(), Button)
            Dim noKey = DirectCast(form.Controls.Find("ClearSeatMenuKey", True).Single(), Button)
            Dim button = DirectCast(form.Controls.Find("SeatMenuButton", True).Single(), Button)
            Dim recenter = DirectCast(form.Controls.Find("RecenterKey", True).Single(), Button)
            Dim notice = form.Controls.Find("Notice", True).Single()
            tabs.SelectedTab = tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Contains(key)) : Application.DoEvents()
            check(key.Text = "F11" AndAlso noKey.Enabled AndAlso button.Text = "Bind a button…" AndAlso Not form.HasUnsavedSettings(), "Controls → Seat shows the F11 default and no button")
            check(Not form.CaptureSeatMenuKey(Keys.F9), "keys pass through until the seat menu key is clicked")
            key.PerformClick()
            check(key.Text = "Press a key…" AndAlso form.CaptureSeatMenuKey(Keys.Up) AndAlso key.Text = "Press a key…" AndAlso notice.Text.Contains("Up can't be used"), "an arrow is refused and capture continues")
            check(form.CaptureSeatMenuKey(Keys.F10) AndAlso notice.Text.Contains("recentering need different keys"), "the recenter key is refused")
            check(form.CaptureSeatMenuKey(Keys.F9 Or Keys.Control) AndAlso key.Text = "Ctrl+F9" AndAlso form.CurrentSettings().SeatMenuKey = 120 AndAlso form.CurrentSettings().SeatMenuModifiers = 1, "a key combination is captured")
            recenter.PerformClick()
            check(form.CaptureRecenterKey(Keys.F9 Or Keys.Control) AndAlso notice.Text.Contains("opens the seat menu") AndAlso form.CaptureRecenterKey(Keys.Escape) AndAlso form.CurrentSettings().RecenterKey = 121,
                  "the recenter key can't take the seat menu's key")
            noKey.PerformClick()
            check(key.Text = "None" AndAlso Not noKey.Enabled AndAlso form.CurrentSettings().SeatMenuKey = 0 AndAlso notice.Text.Contains("seat menu is off"), "No key turns the key off")
            form.SetSeatMenuDevice(PadButton("button:12"))
            check(button.Text = "Bind a button…" AndAlso form.CurrentSettings().SeatMenuButton Is Nothing AndAlso notice.Text.Contains("D-pad, A, B and X"), "a pad's A is refused with the reason")
            form.SetSeatMenuDevice(PadButton("button:15"))
            check(button.Text = "Y on Xbox controller 3" AndAlso form.CurrentSettings().SeatMenuButton?.Input = "button:15", "Y is bound")
            form.SetRecenterDevice(PadButton("button:15"))
            check(form.CurrentSettings().RecenterDevice Is Nothing AndAlso notice.Text.Contains("opens the seat menu"), "recentering can't take the seat menu's button")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.SeatMenuKey = 0 AndAlso saved.SeatMenuButton?.Input = "button:15" AndAlso Not form.HasUnsavedSettings(), "Save settings keeps the key and the button")
            form.ClearSeatMenuKey()
            check(notice.Text.Contains("controller button still opens"), "without a key the button still opens the menu")
            form.SetSeatMenuDevice(Nothing)
            check(button.Text = "Bind a button…" AndAlso form.HasUnsavedSettings(), "Clear removes the button")
            form.Close()
        End Using
    End Sub
End Module
