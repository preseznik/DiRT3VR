Imports DiRT3VR
Imports System.Globalization
Imports System.IO
Imports System.Reflection
Imports System.Windows.Forms

' Driving controls: the bindings model, input capture and the binding wizard (ported from
' GRID2VR's DrivingControlsTests, DrivingCaptureTests and DrivingWizardTests).
Module DrivingControlsTests
    Private Const WheelId As String = "{01234567-0000-0000-0000-504944564944}", WheelProduct As String = "{C262046D-0000-0000-0000-504944564944}"
    Private Const ShifterId As String = "{01234568-0000-0000-0000-504944564944}", ShifterProduct As String = "{00200EB7-0000-0000-0000-504944564944}"

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

    Private Function Throws(action As Action) As Boolean
        Try
            action()
        Catch ex As InvalidOperationException
            Return True
        End Try
        Return False
    End Function

    Private Function Pad(action As String, Optional input As String = "xinput_button_a") As DrivingBinding
        Return New DrivingBinding With {.Action = action, .Device = "Xbox controller 1", .DeviceId = "xinput:0", .DeviceType = "xinput_pad", .Input = input}
    End Function

    Private Function Copy(value As DrivingInput.Sample) As DrivingInput.Sample
        Return DrivingCapture.Clone(value)
    End Function

    Private Function Changed(value As DrivingBinding, change As Action(Of DrivingBinding)) As DrivingBinding
        Dim result = value.Copy()
        change(result)
        Return result
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        RunModel(root, check)
        RunCapture(check)
        RunWizard(check)
    End Sub

    Private Sub RunModel(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "driving-controls")
        Dim initial = DrivingControls.Load(ctx)
        check(Not initial.Enabled AndAlso initial.Bindings.Count = 0 AndAlso initial.ListenAllDevices AndAlso Not File.Exists(DrivingControls.SettingsPath(ctx)), "no driving controls until saved")

        ' The Xbox preset is DiRT 3's own xinput_pad.xml (tools/data/stock-bindings.json).
        Dim preset As New DrivingControls With {.Enabled = True, .Bindings = DrivingControls.XboxPreset()}
        preset.Validate()
        Dim find = Function(action As String) preset.Bindings.Single(Function(b) b.Action = action)
        check(find("Accelerate").Input = "xinput_right_trigger" AndAlso find("Brake").Input = "xinput_left_trigger" AndAlso find("Handbrake").Input = "xinput_button_b" AndAlso
              find("Gear Up").Input = "xinput_button_x" AndAlso find("Gear Down").Input = "xinput_button_a" AndAlso find("Change View").Input = "xinput_button_right_shoulder" AndAlso
              find("Look Back").Input = "xinput_button_y" AndAlso find("Pause").Input = "xinput_button_start" AndAlso find("Instant Replay").Input = "xinput_button_left_shoulder" AndAlso
              find("Horn").Input = "xinput_button_right_stick", "the Xbox preset follows DiRT 3's xinput_pad.xml")
        check(find("Steer Left").Calibration = "biDirectionalLower" AndAlso find("Steer Left").DeadZone = 0.2D AndAlso find("Steer Right").Calibration = "biDirectionalUpper" AndAlso
              find("Look Up").Calibration = "biDirectionalUpper" AndAlso find("Look Up").DeadZone = 0.3D AndAlso find("Look Up").Saturation = 0.8D,
              "the preset keeps DiRT 3's directions, dead zones and saturation")
        check(DrivingControls.Actions.All(Function(a) a.StartsWith("Gear ", StringComparison.Ordinal) AndAlso a <> "Gear Up" AndAlso a <> "Gear Down" OrElse a = "Clutch" OrElse preset.Bindings.Any(Function(b) b.Action = a)),
              "the preset binds every action but the H-pattern gears and clutch, which a pad doesn't have")

        preset.Save(ctx)
        Dim roundtrip = DrivingControls.Load(ctx)
        check(roundtrip.Enabled AndAlso roundtrip.Bindings.Count = preset.Bindings.Count AndAlso roundtrip.Bindings.Zip(preset.Bindings).All(Function(p) p.First.SameAs(p.Second)), "driving controls survive a save")
        Dim saved = File.ReadAllText(DrivingControls.SettingsPath(ctx))
        check(Not saved.Contains("""Keyboard""", StringComparison.Ordinal) AndAlso saved.Contains("""DeviceType""", StringComparison.Ordinal), "the file holds no computed keyboard flag")

        For Each broken In {Changed(preset.Bindings(0), Sub(b) b.Action = "Hand Brake"),
                            Changed(preset.Bindings(0), Sub(b) b.Action = "Boost"),
                            Changed(preset.Bindings(0), Sub(b) b.DeviceType = "keyboard"),
                            Changed(preset.Bindings(0), Sub(b) b.Input = "xinput_invented"),
                            Changed(preset.Bindings(0), Sub(b) b.DeviceId = "xinput:4"),
                            Changed(preset.Bindings(0), Sub(b)
                                                            b.DeadZone = 0.8D : b.Saturation = 0.8D
                                                        End Sub),
                            Changed(preset.Bindings(0), Sub(b)
                                                            b.DeadZone = 0.1000000000000000000000000001D : b.Saturation = 0.1000000000000000000000000002D
                                                        End Sub),
                            Changed(preset.Bindings(0), Sub(b) b.Calibration = "biDirLower"),
                            Changed(preset.Bindings(0), Sub(b) b.Device = "bad" & vbLf & "name"),
                            Changed(preset.Bindings(0), Sub(b) b.DeviceType = "../../keyboard")}
            check(Refused(Sub() Call New DrivingControls With {.Enabled = True, .Bindings = New List(Of DrivingBinding) From {broken}}.Save(ctx)), "an invalid binding is refused: " & broken.Action & " " & broken.Input & " " & broken.DeviceType)
        Next
        check(File.ReadAllText(DrivingControls.SettingsPath(ctx)) = saved, "a refused save leaves the saved controls alone")
        check(Refused(Sub() Call New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {preset.Bindings(0), preset.Bindings(0)}}.Validate()), "one binding per action and device")
        Dim sameSteering As New DrivingControls With {.Enabled = True, .Bindings = New List(Of DrivingBinding) From {preset.Bindings(0), Changed(preset.Bindings(0), Sub(b) b.Action = "Steer Right")}}
        check(sameSteering.Problems().Count = 1 AndAlso Refused(Sub() sameSteering.Save(ctx)), "left and right steering in the same direction is refused")
        check(Refused(Sub() Call New DrivingControls With {.Enabled = True, .Bindings = New List(Of DrivingBinding) From {Changed(find("Brake"), Sub(b) b.Calibration = "uniDirectionalNegative")}}.Save(ctx)), "an inverted Xbox trigger is refused")

        Dim keyboard As New DrivingBinding With {.Action = "Accelerate", .Device = "Keyboard", .DeviceType = "keyboard", .DeviceId = "keyboard", .Input = DrivingControls.KeyInput(Keys.W)}
        check(keyboard.Input = "vk_code_0x57" AndAlso DrivingControls.KeyInput(Keys.Up) = "vk_code_0x26" AndAlso DrivingControls.KeyInput(Keys.Escape) = "vk_code_0x1B" AndAlso
              DrivingControls.KeyInput(Keys.LWin) Is Nothing AndAlso DrivingControls.KeyInput(Keys.W Or Keys.Shift) Is Nothing, "keys use DiRT 3's vk_code names")
        check(DrivingCapture.InputDescription(keyboard) = "W" AndAlso DrivingCapture.Description(keyboard) = "W", "a key is described by its name")
        roundtrip.Assign(keyboard) : roundtrip.Validate()
        check(roundtrip.Bindings.Count = preset.Bindings.Count + 1, "a key binding sits beside the pad's")
        roundtrip.Assign(Changed(keyboard, Sub(b) b.Input = DrivingControls.KeyInput(Keys.Up)))
        check(roundtrip.Bindings.Count = preset.Bindings.Count + 1 AndAlso roundtrip.Bindings.Single(Function(b) b.Keyboard).Input = "vk_code_0x26", "rebinding a key replaces it")

        ' The same action is independent on the keyboard, the pad and each physical wheel.
        Dim wheelThrottle As New DrivingBinding With {.Action = "Accelerate", .Device = "Test wheel", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_y_axis", .Calibration = "uniDirectionalNegative", .DeadZone = 0.03D}
        Dim secondWheel = Changed(wheelThrottle, Sub(b)
                                                     b.DeviceId = "{ABCDEFAB-0000-0000-0000-504944564944}" : b.Device = "Second wheel"
                                                 End Sub)
        Dim shifter = Changed(wheelThrottle, Sub(b)
                                                 b.DeviceId = ShifterId : b.DeviceType = ShifterProduct : b.Device = "Separate shifter" : b.Action = "Gear 1" : b.Input = "di_button_0" : b.Calibration = "uniDirectionalPositive"
                                             End Sub)
        Dim multi As New DrivingControls With {.Enabled = True, .Bindings = New List(Of DrivingBinding) From {keyboard, wheelThrottle, secondWheel, shifter}}
        multi.ApplyXboxPreset() : multi.Validate()
        check(multi.Bindings.Contains(keyboard) AndAlso multi.Bindings.Contains(wheelThrottle) AndAlso multi.Bindings.Contains(secondWheel) AndAlso multi.Bindings.Contains(shifter) AndAlso
              multi.Bindings.Where(Function(b) b.Action = "Accelerate").Count() = 4, "the Xbox preset keeps every other device's bindings")
        multi.Assign(Changed(secondWheel, Sub(b)
                                              b.Input = "di_z_axis" : b.DeviceId = b.DeviceId.ToLowerInvariant()
                                          End Sub))
        check(multi.Bindings.Where(Function(b) b.Action = "Accelerate").Count() = 4 AndAlso multi.Bindings.Contains(wheelThrottle), "a device's identity is matched without case")
        check(Refused(Sub() Call New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {wheelThrottle, Changed(shifter, Sub(b)
                                                                                                                                         b.DeviceId = WheelId : b.DeviceType = "{11111111-0000-0000-0000-504944564944}"
                                                                                                                                     End Sub)}}.Validate()), "one device can't have two controller types")
        Dim independent As New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {
            Changed(wheelThrottle, Sub(b)
                                       b.Action = "Steer Left" : b.Input = "di_x_axis" : b.Calibration = "biDirectionalLower"
                                   End Sub),
            Changed(secondWheel, Sub(b)
                                     b.Action = "Steer Right" : b.Input = "di_x_axis" : b.Calibration = "biDirectionalLower"
                                 End Sub)}}
        check(independent.Problems().Count = 0, "steering on two different wheels is no conflict")
        independent.Assign(Changed(independent.Bindings(0), Sub(b) b.Action = "Steer Right"))
        check(independent.Problems().Count = 1, "the same direction for both on one wheel is")
        Dim limit As New DrivingControls With {.Bindings = Enumerable.Range(1, DrivingControls.MaxDevices).Select(Function(i) Changed(wheelThrottle, Sub(b) b.DeviceId = "{" & i.ToString("X8", CultureInfo.InvariantCulture) & "-0000-0000-0000-504944564944}")).ToList()}
        limit.Validate() : limit.Assign(keyboard)
        check(Refused(Sub() limit.Validate()), "at most 16 devices")
        For Each good In {"di_button_0", "di_button_127", "di_dpad_3_left", "di_slider_1", "di_z_axis_rotation"}
            Dim value = good
            check(Not Refused(Sub() Call New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {Changed(wheelThrottle, Sub(b) b.Input = value)}}.Validate()), "DiRT 3 input " & good & " is accepted")
        Next
        For Each bad In {"di_button_128", "di_button_01", "di_dpad_4_up", "di_slider_2", "xinput_button_a", "vk_code_0x20"}
            Dim value = bad
            check(Refused(Sub() Call New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {Changed(wheelThrottle, Sub(b) b.Input = value)}}.Validate()), "invalid wheel input " & bad & " is refused")
        Next

        ' Presets are portable controls files; a bad file never replaces the saved controls.
        Dim presetFile = Path.Combine(DrivingControls.PresetsPath(ctx), "Rig A.json")
        multi.SaveFile(presetFile)
        Dim loaded = DrivingControls.LoadFile(presetFile)
        check(loaded.Bindings.Count = multi.Bindings.Count AndAlso loaded.Enabled, "a driving preset loads back")
        File.WriteAllText(presetFile, "{""Version"":1,""Bindings"":[{""Action"":""Brake"",""DeviceId"":""keyboard"",""Device"":""Keyboard"",""DeviceType"":""keyboard"",""Input"":""vk_code_0x00""}]}")
        check(Refused(Sub() DrivingControls.LoadFile(presetFile)), "a preset with an invalid key is refused")
        File.WriteAllText(presetFile, "{not json")
        check(Refused(Sub() DrivingControls.LoadFile(presetFile)), "a damaged preset is refused")
        File.WriteAllText(presetFile, "{""Version"":2,""Bindings"":[]}")
        check(Refused(Sub() DrivingControls.LoadFile(presetFile)), "a newer controls version is refused")
        ' A clutch axis saved at the earlier 0% / 100% loads with the clutch's default; other calibrations stay.
        Dim clutchAxis As New DrivingBinding With {.Action = "Clutch", .Device = "Test wheel", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_slider_0", .Calibration = "uniDirectionalNegative"}
        Call New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {clutchAxis, Changed(wheelThrottle, Sub(b) b.DeadZone = 0D),
            New DrivingBinding With {.Action = "Clutch", .Device = "Separate shifter", .DeviceId = ShifterId, .DeviceType = ShifterProduct, .Input = "di_button_3"}}}.SaveFile(presetFile)
        Dim migrated = DrivingControls.LoadFile(presetFile)
        Dim movedClutch = migrated.Bindings.Single(Function(b) b.Input = "di_slider_0")
        check(movedClutch.DeadZone = 0.1D AndAlso movedClutch.Saturation = 0.8D, "a clutch axis at 0% / 100% loads at 10% / 80%")
        check(migrated.Bindings.Single(Function(b) b.Action = "Accelerate").Saturation = 1D AndAlso migrated.Bindings.Single(Function(b) b.Input = "di_button_3").Saturation = 1D,
              "other axes and a clutch button keep 0% / 100%")
        Call New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {Changed(clutchAxis, Sub(b)
                                                                                                                b.DeadZone = 0.05D : b.Saturation = 0.9D
                                                                                                            End Sub)}}.SaveFile(presetFile)
        check(DrivingControls.LoadFile(presetFile).Bindings.Single().Saturation = 0.9D, "a calibrated clutch keeps its calibration")

        ' Wheel rotation… (#a72b44ea, as DiRT2VR 0.18): steering saturation = DiRT 3's rotation / the wheel's.
        Const G920 As String = "Logitech G HUB G920 Driving Force Racing Wheel USB"
        check(DrivingControls.SuggestedWheelRotation(G920) = 900 AndAlso DrivingControls.SuggestedWheelRotation("Logitech G PRO Racing Wheel") = 1080 AndAlso
              DrivingControls.SuggestedWheelRotation("Thrustmaster T300RS Racing wheel") = 1080 AndAlso DrivingControls.SuggestedWheelRotation("FANATEC CSL DD") = 0 AndAlso
              DrivingControls.SuggestedWheelRotation(Nothing) = 0, "wheels with a fixed range get their maximum suggested; others none")
        Dim steer = Function(action As String, calibration As String) New DrivingBinding With {.Action = action, .Device = G920, .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_x_axis", .Calibration = calibration}
        Dim rotation As New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {steer("Steer Right", "biDirectionalUpper"), steer("Steer Left", "biDirectionalLower"), wheelThrottle.Copy()}}
        check(If(rotation.SteeringWheel()?.Length, 0) = 2 AndAlso rotation.SteeringWheel()(0).Action = "Steer Left", "the wheel with Steer Left and Steer Right is the steering wheel")
        check(New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {steer("Steer Left", "biDirectionalLower")}}.SteeringWheel() Is Nothing AndAlso
              New DrivingControls With {.Bindings = DrivingControls.XboxPreset()}.SteeringWheel() Is Nothing, "one steering direction, or an Xbox controller, is no steering wheel")
        rotation.ApplyWheelRotation(900, 540)
        check(rotation.SteeringWheel().All(Function(b) b.Saturation = 0.6D) AndAlso rotation.WheelRotation = 900 AndAlso rotation.Bindings.Single(Function(b) b.Action = "Accelerate").Saturation = 1D,
              "540° of a 900° wheel: steering saturation 60%, the wheel's rotation kept, pedals unchanged")
        check(SessionActionMaps.AxisOf(rotation.SteeringWheel()(0)).Saturation = "0.6", "and the session map writes it")
        check(Refused(Sub() rotation.ApplyWheelRotation(900, 1080)) AndAlso Refused(Sub() rotation.ApplyWheelRotation(100, 90)) AndAlso Refused(Sub() rotation.ApplyWheelRotation(2520, 90)),
              "more than the wheel, a wheel under 180°, or under a tenth of the wheel is refused")
        rotation.SteeringWheel()(0).DeadZone = 0.5D
        check(Refused(Sub() rotation.ApplyWheelRotation(900, 360)) AndAlso rotation.SteeringWheel().All(Function(b) b.Saturation = 0.6D), "a saturation at or under the dead zone is refused and nothing changes")
        check(Refused(Sub() Call New DrivingControls().ApplyWheelRotation(900, 540)), "without a steering wheel it's refused")
        rotation.SteeringWheel()(0).DeadZone = 0D
        rotation.SaveFile(presetFile)
        check(DrivingControls.LoadFile(presetFile).WheelRotation = 900, "the wheel's rotation is saved with the controls and presets")
        File.WriteAllText(presetFile, "{""Version"":1,""WheelRotation"":50,""Bindings"":[]}")
        check(Refused(Sub() DrivingControls.LoadFile(presetFile)), "an impossible wheel rotation is refused")
        check(File.ReadAllText(DrivingControls.SettingsPath(ctx)) = saved, "loading presets doesn't touch the saved controls")
    End Sub

    Private Sub RunCapture(check As Action(Of Boolean, String))
        Dim wheel As New DrivingInput.Device(0, WheelId, "Wheel", WheelProduct)
        Dim shifter As New DrivingInput.Device(1, ShifterId, "Separate shifter", ShifterProduct)
        Dim xbox As New DrivingInput.Device(2, "xinput:0", "Xbox controller 1", "xinput_pad")
        Dim rest = DrivingInput.Sample.Empty() : rest.Connected = 1UI : rest.Axes = 3UI : rest.Values(1) = 1

        ' A press before the axes settle must not become the held baseline.
        Dim first As New DrivingCapture(wheel, "Gear Up") : first.Update(rest, 0)
        Dim pressed = Copy(rest) : pressed.Buttons(5) = 1
        first.Update(pressed, 40)
        check(first.WaitingForRelease AndAlso first.Detected?.Input = "di_button_5" AndAlso Not first.AxesReady, "a first button press before the axes settle is seen")
        first.Update(rest, 80) : first.Update(rest, 240)
        check(first.Completed?.Input = "di_button_5" AndAlso first.Completed.DeviceType = WheelProduct AndAlso first.Completed.Device = "Wheel", "the press completes on release")

        Dim held As New DrivingCapture(wheel, "Gear Up") : held.Update(pressed, 0) : held.Update(pressed, 700)
        check(Not held.WaitingForRelease, "a button held from the start is ignored")
        held.Update(rest, 740) : held.Update(pressed, 780)
        check(held.WaitingForRelease, "and works after a release")
        Dim another As New DrivingCapture(wheel, "Gear Down") : another.Update(pressed, 0)
        Dim two = Copy(pressed) : two.Buttons(6) = 1 : another.Update(two, 40)
        check(another.Detected?.Input = "di_button_6", "one held button can't block another")

        Dim noisy As New DrivingCapture(wheel, "Handbrake") : noisy.Update(rest, 0)
        Dim chatter = Copy(rest)
        For time = 40 To 560 Step 40
            chatter.Values(0) = If(time Mod 80 = 0, 0.8F, -0.8F) : chatter.Values(1) = -chatter.Values(0) : noisy.Update(chatter, time)
        Next
        chatter.Buttons(127) = 1 : noisy.Update(chatter, 580)
        check(noisy.Detected?.Input = "di_button_127" AndAlso noisy.WaitingForRelease, "the highest button is caught amid axis chatter")

        Dim axisAndButton = Copy(rest) : axisAndButton.Values(1) = -1 : axisAndButton.Buttons(7) = 1
        check(DrivingCapture.Detect("Handbrake", wheel, rest, axisAndButton)?.Input = "di_button_7", "a button wins over incidental pedal travel")
        check(DrivingCapture.Detect("Handbrake", wheel, rest, axisAndButton, axesOnly:=True)?.Input = "di_y_axis", "Axis only still finds the axis")

        Dim hat = Copy(rest) : hat.Pov(0) = 9000
        Dim hatCapture As New DrivingCapture(wheel, "Menu Right") : hatCapture.Update(rest, 0) : hatCapture.Update(hat, 40)
        check(hatCapture.WaitingForRelease AndAlso hatCapture.Detected?.Input = "di_dpad_0_right", "a hat direction is DiRT 3's di_dpad_0_right")
        Dim diagonal = Copy(hat) : diagonal.Pov(0) = 4500 : hatCapture.Update(diagonal, 80) : hatCapture.Update(diagonal, 300)
        check(hatCapture.Completed Is Nothing, "a diagonal is not a release")
        hatCapture.Update(rest, 340) : hatCapture.Update(rest, 500)
        check(hatCapture.Completed?.Input = "di_dpad_0_right", "the hat completes at neutral")
        Dim heldHat As New DrivingCapture(wheel, "Menu Up") : heldHat.Update(diagonal, 0) : hat.Pov(0) = 0 : heldHat.Update(hat, 100)
        check(Not heldHat.WaitingForRelease, "a diagonal held from the start can't arm a direction")
        heldHat.Update(rest, 140) : heldHat.Update(hat, 180)
        check(heldHat.Detected?.Input = "di_dpad_0_up", "the hat arms after neutral")

        Dim pedal As New DrivingCapture(wheel, "Brake") : pedal.Update(rest, 0) : pedal.Update(rest, 600)
        check(pedal.AxesReady, "a pedal's rest settles")
        Dim down = Copy(rest) : down.Values(1) = -1 : pedal.Update(down, 640)
        check(Not pedal.WaitingForRelease, "axis travel needs a deliberate hold")
        pedal.Update(down, 740)
        check(pedal.WaitingForRelease AndAlso pedal.Detected?.Calibration = "uniDirectionalNegative", "the pedal's direction uses DiRT 3's calibrator names")
        pedal.Update(rest, 780) : pedal.Update(rest, 940)
        check(pedal.Completed?.Input = "di_y_axis", "the pedal completes on release")

        Dim analog As New DrivingCapture(wheel, "Handbrake") : analog.Update(rest, 0) : analog.Update(rest, 600) : analog.Update(down, 640) : analog.Update(down, 740)
        check(analog.WaitingForRelease AndAlso DrivingCapture.IsAxis(analog.Detected), "an analog candidate is held")
        Dim overrideButton = Copy(down) : overrideButton.Buttons(9) = 1 : analog.Update(overrideButton, 780)
        check(analog.Detected?.Input = "di_button_9", "a button replaces an axis stuck on the same device")

        Dim wheelCapture As New DrivingCapture(wheel, "Gear 1") : wheelCapture.Update(rest, 0) : wheelCapture.Update(rest, 600) : wheelCapture.Update(down, 640) : wheelCapture.Update(down, 740)
        Dim gearCapture As New DrivingCapture(shifter, "Gear 1")
        Dim gearRest = Copy(rest) : gearRest.Axes = 0UI : gearCapture.Update(gearRest, 0)
        Dim gear = Copy(gearRest) : gear.Buttons(2) = 1 : gearCapture.Update(gear, 780)
        check(DrivingCapture.ChooseActive({wheelCapture, gearCapture}, wheelCapture) Is gearCapture, "a separate shifter wins over a held wheel axis")
        check(DrivingCapture.ChooseActive({wheelCapture, gearCapture}, gearCapture) Is gearCapture, "the active button stays chosen until released")
        gearCapture.Update(gearRest, 820) : gearCapture.Update(gearRest, 980)
        check(gearCapture.Completed?.DeviceId = ShifterId, "the shifter keeps its own identity")

        Dim missing = Copy(rest) : missing.Connected = 0UI : analog.Update(missing, 800)
        check(Not analog.Ready AndAlso Not analog.WaitingForRelease AndAlso analog.Completed Is Nothing, "a disconnect resets the capture")
        analog.Update(overrideButton, 840) : analog.Update(overrideButton, 1500)
        check(Not analog.WaitingForRelease, "reconnecting with a held button is no press")
        held.Reset() : held.Update(pressed, 1000) : held.Update(pressed, 1700)
        check(Not held.WaitingForRelease, "a reset can't keep an earlier press")
        Dim bad = Copy(rest) : bad.Values(0) = Single.NaN
        Dim finite As New DrivingCapture(wheel, "Steer Left") With {.AxesOnly = True}
        finite.Update(bad, 0) : finite.Update(rest, 40) : finite.Update(rest, 640)
        Dim left = Copy(rest) : left.Values(0) = -0.8F : finite.Update(left, 680) : finite.Update(left, 780)
        check(finite.Detected?.Input = "di_x_axis" AndAlso finite.Detected.Calibration = "biDirectionalLower" AndAlso finite.WaitingForRelease, "steering recovers from a non-finite first sample")

        ' Xbox: names from DiRT 3's xinput_pad.xml; triggers never inverted; Y up-positive.
        Dim padRest = DrivingInput.Sample.Empty() : padRest.Connected = 1UI : padRest.Axes = &H3FUI
        Dim padButton = Copy(padRest) : padButton.Buttons(13) = 1
        check(DrivingCapture.Detect("Handbrake", xbox, padRest, padButton)?.Input = "xinput_button_b", "an Xbox button uses DiRT 3's name")
        padButton.Buttons(13) = 0 : padButton.Buttons(10) = 1
        check(DrivingCapture.Detect("Handbrake", xbox, padRest, padButton) Is Nothing, "unused XInput bits are ignored")
        Dim trigger = Copy(padRest) : trigger.Values(5) = 1
        Dim accelerate = DrivingCapture.Detect("Accelerate", xbox, padRest, trigger)
        check(accelerate?.Input = "xinput_right_trigger" AndAlso accelerate.Calibration = "uniDirectionalPositive", "a trigger binds increasing")
        Dim stickUp = Copy(padRest) : stickUp.Values(3) = 0.9F
        Dim lookUp = DrivingCapture.Detect("Look Up", xbox, padRest, stickUp)
        check(lookUp?.Input = "xinput_right_stick_y" AndAlso lookUp.Calibration = "biDirectionalUpper", "stick up is biDirectionalUpper, as DiRT 3's Look Up")
        Dim steerPad = Copy(padRest) : steerPad.Values(4) = 0.9F
        check(DrivingCapture.Detect("Steer Left", xbox, padRest, steerPad) Is Nothing, "a trigger can't steer")
        ' #b3f228d0: a wheel's d-pad is a hat; Menu directions bind to DiRT 3's di_dpad names (the G920's d-pad included).
        For Each pair In {(Pov:=0UI, Action:="Menu Up", Name:="di_dpad_0_up"), (Pov:=9000UI, Action:="Menu Right", Name:="di_dpad_0_right"), (Pov:=18000UI, Action:="Menu Down", Name:="di_dpad_0_down"), (Pov:=27000UI, Action:="Menu Left", Name:="di_dpad_0_left")}
            Dim pressedHat = Copy(rest) : pressedHat.Pov(0) = pair.Pov
            check(DrivingCapture.Detect(pair.Action, wheel, rest, pressedHat)?.Input = pair.Name, pair.Action & " binds the wheel's d-pad as " & pair.Name)
        Next
        ' #e7547282: axes bound in the launcher start with no dead zone and full saturation.
        Dim steerRight = Copy(rest) : steerRight.Values(0) = 0.5F
        For Each bound In {DrivingCapture.Detect("Steer Right", wheel, rest, steerRight), DrivingCapture.Detect("Brake", wheel, rest, down), finite.Detected}
            check(bound IsNot Nothing AndAlso bound.DeadZone = 0D AndAlso bound.Saturation = 1D, "a bound axis starts at 0% dead zone and 100% saturation (" & bound?.Input & ")")
        Next
        ' #16fa2aa3: the clutch starts as in DiRT 3's own wheel presets (G25/G27: 10% / 80%).
        Dim clutch = DrivingCapture.Detect("Clutch", wheel, rest, down)
        check(clutch IsNot Nothing AndAlso clutch.Calibration = "uniDirectionalNegative" AndAlso clutch.DeadZone = 0.1D AndAlso clutch.Saturation = 0.8D, "a bound clutch axis starts at 10% dead zone and 80% saturation")
        check(SessionActionMaps.AxisOf(clutch).DeadZone = "0.1" AndAlso SessionActionMaps.AxisOf(clutch).Saturation = "0.8", "and the session map writes it so")
        ' #9ff647cf: which bound inputs are pressed or moved now, for the live highlight.
        Dim bindingFor = Function(action As String, input As String, calibration As String) New DrivingBinding With {.Action = action, .Device = "Wheel", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = input, .Calibration = calibration}
        Dim live = Copy(rest) : live.Buttons(3) = 1 : live.Pov(0) = 0UI : live.Values(0) = -0.5F : live.Values(1) = -0.5F
        check(DrivingCapture.Active(bindingFor("Gear Up", "di_button_3", "uniDirectionalPositive"), live, rest) AndAlso Not DrivingCapture.Active(bindingFor("Gear Down", "di_button_4", "uniDirectionalPositive"), live, rest),
              "a held button is active, others aren't")
        check(DrivingCapture.Active(bindingFor("Menu Up", "di_dpad_0_up", "uniDirectionalPositive"), live, rest) AndAlso Not DrivingCapture.Active(bindingFor("Menu Down", "di_dpad_0_down", "uniDirectionalPositive"), live, rest),
              "a hat direction is active only in its own direction")
        check(DrivingCapture.Active(bindingFor("Steer Left", "di_x_axis", "biDirectionalLower"), live, rest) AndAlso Not DrivingCapture.Active(bindingFor("Steer Right", "di_x_axis", "biDirectionalUpper"), live, rest),
              "steering lights only the direction it's turned")
        check(DrivingCapture.Active(bindingFor("Brake", "di_y_axis", "uniDirectionalNegative"), live, rest) AndAlso Not DrivingCapture.Active(bindingFor("Brake", "di_y_axis", "uniDirectionalNegative"), rest, rest),
              "a pedal is active once pressed from its rest")
        Dim padLive = Copy(padRest) : padLive.Values(5) = 0.6F : padLive.Buttons(12) = 1
        check(DrivingCapture.Active(New DrivingBinding With {.DeviceType = "xinput_pad", .DeviceId = "xinput:0", .Input = "xinput_right_trigger", .Calibration = "uniDirectionalPositive"}, padLive, padRest) AndAlso
              DrivingCapture.Active(New DrivingBinding With {.DeviceType = "xinput_pad", .DeviceId = "xinput:0", .Input = "xinput_button_a"}, padLive, padRest), "an Xbox trigger and button light up")
        Dim gone = Copy(live) : gone.Connected = 0UI
        check(Not DrivingCapture.Active(bindingFor("Gear Up", "di_button_3", "uniDirectionalPositive"), gone, rest) AndAlso
              Not DrivingCapture.Active(New DrivingBinding With {.DeviceType = "keyboard", .DeviceId = "keyboard", .Input = "vk_code_0x41"}, live, rest), "a disconnected device or a key (checked by the window) isn't active here")
        check(DrivingCapture.InputDescription(New DrivingBinding With {.Device = "Wheel", .DeviceType = WheelProduct, .DeviceId = WheelId, .Input = "di_button_2", .Action = "Gear 1"}) = "button 2" AndAlso
              DrivingCapture.Description(lookUp) = "Xbox controller 1 · right stick y (+)" AndAlso DrivingCapture.Description(Nothing) = "DiRT 3's binding", "bindings are described plainly")
    End Sub

    Private Sub RunWizard(check As Action(Of Boolean, String))
        Dim batches = DrivingControls.WizardBatches
        check(batches.Select(Function(b) b.Name).SequenceEqual({"Essentials", "H-pattern shifter", "Flashback and replays", "Menu controls", "Look and horn"}), "the wizard's batch order")
        Dim all = batches.SelectMany(Function(b) b.Actions).ToArray()
        check(all.Length = DrivingControls.Actions.Length AndAlso all.Distinct().Count() = all.Length AndAlso DrivingControls.Actions.All(Function(a) all.Contains(a)), "the batches cover every action exactly once")
        check({"Steer Left", "Steer Right", "Accelerate", "Brake", "Handbrake", "Gear Up", "Gear Down", "Pause"}.All(Function(a) batches(0).Actions.Contains(a)), "essentials hold the driving basics and Pause")
        check(batches(1).Actions.SequenceEqual(Enumerable.Range(1, 6).Select(Function(i) "Gear " & i.ToString(CultureInfo.InvariantCulture)).Append("Gear Reverse")), "the H-pattern batch is DiRT 3's six gears and reverse")
        check(DrivingControls.Actions.Where(Function(a) a.StartsWith("Menu ", StringComparison.Ordinal)).OrderBy(Function(a) a).SequenceEqual(batches(3).Actions.OrderBy(Function(a) a)), "the menu batch")

        Dim wizard As New DrivingWizard(batches)
        check(wizard.Action = "Steer Left" AndAlso Not wizard.Asking AndAlso Not wizard.Done AndAlso wizard.BatchIndex = 0, "the wizard starts with essentials")
        check(Throws(Sub() wizard.Accept()) AndAlso Throws(Sub() wizard.Skip()) AndAlso Throws(Sub() wizard.Record(Pad("Brake"))), "no accept, skip or other action while capturing")
        For Each action In batches(0).Actions
            wizard.Record(If(action = "Clutch", Nothing, Pad(action)))
        Next
        check(wizard.Asking AndAlso wizard.Action Is Nothing AndAlso wizard.BatchIndex = 1 AndAlso wizard.FinishedBatches = 1, "it asks before the H-pattern batch")
        check(wizard.Bindings.Count = batches(0).Actions.Length - 1, "a skipped step adds nothing")
        For i = 2 To batches.Length - 1
            wizard.Skip()
            check(wizard.Asking AndAlso wizard.BatchIndex = i, "it asks batch by batch (" & i & ")")
        Next
        wizard.Skip()
        check(wizard.Done AndAlso Not wizard.Asking AndAlso wizard.Action Is Nothing AndAlso wizard.FinishedBatches = 1, "skipping the last batch finishes")

        ' Skipped and cancelled batches keep the existing bindings.
        Dim shifterGear As New DrivingBinding With {.Action = "Gear 1", .Device = "Separate shifter", .DeviceId = ShifterId, .DeviceType = ShifterProduct, .Input = "di_button_0"}
        Dim existing As New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {Pad("Gear 1", "xinput_button_y"), Pad("Instant Replay", "xinput_button_y"), Pad("Menu Up", "xinput_dpad_up"), shifterGear}}
        Dim before = existing.Bindings.ToArray()
        wizard = New DrivingWizard(batches)
        For Each action In batches(0).Actions
            wizard.Record(Pad(action))
        Next
        wizard.Skip()
        check(wizard.Batch.Name = "Flashback and replays", "skip moves to the next question")
        wizard.Accept() : wizard.Record(Pad("Instant Replay", "xinput_button_b")) : wizard.Finish()
        check(wizard.Done AndAlso wizard.Bindings.Count = batches(0).Actions.Length, "an unfinished batch is dropped")
        For Each binding In wizard.Bindings
            existing.Assign(binding)
        Next
        existing.Validate()
        check(before.All(Function(b) existing.Bindings.Contains(b)) AndAlso existing.Bindings.Count = before.Length + batches(0).Actions.Length, "bindings outside the finished batches are untouched")

        wizard = New DrivingWizard(batches)
        Do Until wizard.Done
            If wizard.Asking Then wizard.Accept() Else wizard.Record(Pad(wizard.Action))
        Loop
        check(wizard.FinishedBatches = batches.Length AndAlso wizard.Bindings.Select(Function(b) b.Action).SequenceEqual(all), "accepting every batch binds every action")
        Dim empty = False
        Try
            Dim unused As New DrivingWizard(Array.Empty(Of DrivingWizardBatch)())
        Catch ex As ArgumentException
            empty = True
        End Try
        check(empty, "an empty wizard is refused")
    End Sub

    Private Function Field(Of T)(form As Object, name As String) As T
        Return DirectCast(form.GetType().GetField(name, BindingFlags.NonPublic Or BindingFlags.Instance).GetValue(form), T)
    End Function

    Private Sub Answer(form As DrivingBindingForm, binding As DrivingBinding)
        GetType(DrivingBindingForm).GetMethod("NextStep", BindingFlags.NonPublic Or BindingFlags.Instance).Invoke(form, New Object() {binding})
    End Sub

    Private Sub Click(button As Button)
        GetType(Button).GetMethod("OnClick", BindingFlags.NonPublic Or BindingFlags.Instance).Invoke(button, New Object() {EventArgs.Empty})
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim batches = DrivingControls.WizardBatches
        Dim noInput = Path.Combine(root, "no-driving-input.dll")
        Using form As New DrivingBindingForm(noInput, Nothing, False)
            Dim heading = Field(Of Label)(form, "heading"), batchLabel = Field(Of Label)(form, "batchLabel")
            Dim captureButtons = Field(Of FlowLayoutPanel)(form, "captureButtons"), question = Field(Of FlowLayoutPanel)(form, "batchButtons")
            Dim source = Field(Of ComboBox)(form, "source"), bindBatch = Field(Of Button)(form, "bindBatch")
            Dim layout = form.Controls(0) : form.Controls.Remove(layout)
            Try
                layout.Visible = True
                check(heading.Text = "STEER LEFT  (1/11)" AndAlso batchLabel.Text = "Essentials — batch 1 of 5" AndAlso batchLabel.Visible, "the wizard opens on Steer Left (" & heading.Text & ")")
                check(captureButtons.Visible AndAlso source.Visible AndAlso Not question.Visible, "capture controls start visible")
                For Each action In batches(0).Actions
                    Answer(form, Pad(action))
                Next
                check(heading.Text = "H-PATTERN SHIFTER?" AndAlso batchLabel.Text = "Next: H-pattern shifter — batch 2 of 5" AndAlso bindBatch.Text = "Bind H-pattern shifter", "the H-pattern question")
                check(question.Visible AndAlso Not captureButtons.Visible AndAlso Not source.Visible, "the question hides the capture controls")
                Click(DirectCast(question.Controls.Find("WizardSkipBatch", False).Single(), Button))
                check(heading.Text = "FLASHBACK AND REPLAYS?" AndAlso question.Visible, "Skip batch asks about the next one")
                Click(bindBatch)
                check(heading.Text = "INSTANT REPLAY  (1/9)" AndAlso captureButtons.Visible AndAlso Not question.Visible, "accepting starts that batch")
                Answer(form, Pad("Instant Replay"))
            Finally
                form.Controls.Add(layout)
            End Try
            Click(captureButtons.Controls.OfType(Of Button)().Single(Function(b) b.Text = "Cancel"))
            check(form.DialogResult = DialogResult.OK AndAlso form.Bindings.Count = batches(0).Actions.Length AndAlso form.Bindings.All(Function(b) b.Action <> "Instant Replay"), "Cancel keeps finished batches only")
        End Using
        Using form As New DrivingBindingForm(noInput, Nothing, False)
            Answer(form, Pad("Steer Left"))
            Click(Field(Of FlowLayoutPanel)(form, "captureButtons").Controls.OfType(Of Button)().Single(Function(b) b.Text = "Cancel"))
            check(form.DialogResult = DialogResult.Cancel AndAlso form.Bindings.Count = 0, "Cancel during essentials keeps nothing")
        End Using
        Using form As New DrivingBindingForm(noInput, "Gear 3", False)
            Dim layout = form.Controls(0) : form.Controls.Remove(layout)
            Try
                layout.Visible = True
                check(Field(Of Label)(form, "heading").Text = "GEAR 3" AndAlso Not Field(Of Label)(form, "batchLabel").Visible, "binding one action has no batches")
            Finally
                form.Controls.Add(layout)
            End Try
            Answer(form, Changed(Pad("Gear 3"), Sub(b) b.Input = "xinput_button_x"))
            check(form.DialogResult = DialogResult.OK AndAlso form.Bindings.Single().Input = "xinput_button_x", "one action's binding is returned")
        End Using

        ' The controls window: wizard results and presets change only the edited copy until Save.
        Dim ctx = Context(root, "driving-controls-ui")
        Using form As New DrivingControlsForm(ctx, noInput)
            form.Show() : Application.DoEvents()
            Dim rows = DirectCast(form.Controls.Find("DrivingRows", True).Single(), ListView)
            check(rows.Items.Count = DrivingControls.Actions.Length AndAlso rows.Items.Cast(Of ListViewItem)().All(Function(r) r.SubItems(1).Text = "DiRT 3's binding"), "every action starts on DiRT 3's binding")
            check(form.Controls.Find("DrivingWheelRotation", True).Single().Text = "Wheel rotation…", "Setup has Wheel rotation…")
            DirectCast(form.Controls.Find("DrivingXboxPreset", True).Single(), Button).PerformClick() : Application.DoEvents()
            check(form.Current.Enabled AndAlso form.Current.Bindings.Count = DrivingControls.XboxPreset().Count AndAlso
                  rows.Items.Cast(Of ListViewItem)().First(Function(r) r.Text = "Accelerate").SubItems(2).Text = "right trigger", "the Xbox preset fills the list and switches the bindings on")
            check(Not File.Exists(DrivingControls.SettingsPath(ctx)), "nothing is saved before Save")
            form.ApplyBindings({New DrivingBinding With {.Action = "Gear 1", .Device = "Separate shifter", .DeviceId = ShifterId, .DeviceType = ShifterProduct, .Input = "di_button_0"}})
            check(rows.Items.Cast(Of ListViewItem)().Any(Function(r) r.Text = "Gear 1" AndAlso r.SubItems(1).Text = "Separate shifter" AndAlso r.SubItems(2).Text = "button 0"), "a captured gear shows in the list")
            Dim gearRow = rows.Items.Cast(Of ListViewItem)().First(Function(r) r.Text = "Gear 1")
            gearRow.Selected = True : Application.DoEvents()
            check(DirectCast(form.Controls.Find("DrivingCalibrate", True).Single(), Button).Enabled AndAlso DirectCast(form.Controls.Find("DrivingRemoveBinding", True).Single(), Button).Enabled, "a selected device binding can be calibrated or removed")
            DirectCast(form.Controls.Find("DrivingRemoveBinding", True).Single(), Button).PerformClick() : Application.DoEvents()
            check(Not form.Current.Bindings.Any(Function(b) b.Action = "Gear 1"), "Remove drops the binding")
            DirectCast(form.Controls.Find("DrivingSave", True).Single(), Button).PerformClick() : Application.DoEvents()
            check(File.Exists(DrivingControls.SettingsPath(ctx)) AndAlso DrivingControls.Load(ctx).Bindings.Count = DrivingControls.XboxPreset().Count, "Save writes the edited bindings")
        End Using
        ' The live highlight: rows light up while their input is held (devices) or their key is down.
        Using form As New DrivingControlsForm(ctx, noInput)
            form.UsePreset(New DrivingControls With {.Enabled = True, .Bindings = New List(Of DrivingBinding) From {
                New DrivingBinding With {.Action = "Gear Up", .Device = "Wheel", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_button_3"},
                New DrivingBinding With {.Action = "Pause", .Device = "Keyboard", .DeviceId = "keyboard", .DeviceType = "keyboard", .Input = "vk_code_0x1B"}}})
            Dim resting = DrivingInput.Sample.Empty() : resting.Connected = 1UI
            Dim held = DrivingCapture.Clone(resting) : held.Buttons(3) = 1
            form.ShowActive(New Dictionary(Of String, DrivingInput.Sample) From {{WheelId, resting}}, Function(key) False)
            check(form.HighlightedActions.Count = 0, "nothing is lit at rest")
            form.ShowActive(New Dictionary(Of String, DrivingInput.Sample) From {{WheelId.ToLowerInvariant(), held}}, Function(key) key = &H1B)
            check(form.HighlightedActions.OrderBy(Function(a) a).SequenceEqual({"Gear Up", "Pause"}), "a held wheel button and a held key light their rows (" & String.Join(", ", form.HighlightedActions) & ")")
            form.ShowActive(New Dictionary(Of String, DrivingInput.Sample) From {{WheelId, resting}}, Function(key) False)
            check(form.HighlightedActions.Count = 0, "released inputs go back to normal")
        End Using
        Using form As New DrivingControlsForm(ctx, noInput)
            check(form.Current.Enabled AndAlso form.Current.Bindings.Count = DrivingControls.XboxPreset().Count, "the window reopens with the saved bindings")
            form.UsePreset(New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {Pad("Pause", "xinput_button_back")}})
            check(Not form.Current.Enabled AndAlso form.Current.Bindings.Count = 1 AndAlso DrivingControls.Load(ctx).Bindings.Count = DrivingControls.XboxPreset().Count, "a loaded preset replaces the edits, not the saved file")
        End Using
        ' Controls → Driving controls in the main window: off until bindings are saved, then a one-line summary.
        Dim mainCtx = Context(root, "driving-controls-main")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(mainCtx)
        Using form As New MainForm(mainCtx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim find = Function(name As String) form.Controls.Find(name, True).Single()
            check(find("DrivingControls").Text = "Driving controls…" AndAlso find("DrivingSummary").Text.StartsWith("Off:", StringComparison.Ordinal) AndAlso find("SectionDrivingcontrols") IsNot Nothing,
                  "Controls has a Driving controls section, off by default (" & find("DrivingSummary").Text & ")")
        End Using
        Call New DrivingControls With {.Enabled = True, .Bindings = DrivingControls.XboxPreset()}.Save(mainCtx)
        Using form As New MainForm(mainCtx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(form.Controls.Find("DrivingSummary", True).Single().Text = "On for direct practice: " & DrivingControls.XboxPreset().Count & " bindings on Xbox controller 1.", "the summary counts the saved bindings and names the devices")
        End Using
        Using calibration As New DrivingCalibrationForm(New DrivingBinding With {.Action = "Brake", .Device = "Wheel", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_y_axis", .Calibration = "uniDirectionalNegative", .DeadZone = 0.05D, .Saturation = 0.9D})
            check(Field(Of ComboBox)(calibration, "direction").SelectedIndex = 1 AndAlso calibration.Binding.DeadZone = 0.05D AndAlso calibration.Binding.Saturation = 0.9D, "calibration starts from the binding")
            Field(Of NumericUpDown)(calibration, "dead").Value = 10 : Field(Of NumericUpDown)(calibration, "saturationBox").Value = 80
            check(calibration.Binding.DeadZone = 0.1D AndAlso calibration.Binding.Saturation = 0.8D AndAlso calibration.Binding.Calibration = "uniDirectionalNegative", "calibration edits dead zone and saturation")
        End Using
        Using calibration As New DrivingCalibrationForm(New DrivingBinding With {.Action = "Clutch", .Device = "Wheel", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_slider_0", .Calibration = "uniDirectionalNegative", .DeadZone = 0.3D, .Saturation = 0.95D})
            calibration.ShowInTaskbar = False : calibration.StartPosition = FormStartPosition.Manual : calibration.Location = New Drawing.Point(-30000, -30000)
            calibration.Show() : Application.DoEvents()
            Dim reset = DirectCast(calibration.Controls.Find("CalibrationReset", True).Single(), Button)
            check(reset.Text = "Reset to 10% / 80%", "the clutch's reset names its own default (" & reset.Text & ")")
            reset.PerformClick()
            check(calibration.Binding.DeadZone = 0.1D AndAlso calibration.Binding.Saturation = 0.8D, "and resets to it (" & calibration.Binding.DeadZone & " / " & calibration.Binding.Saturation & ")")
            calibration.Close()
        End Using
        Dim steering As New DrivingControls With {.Bindings = New List(Of DrivingBinding) From {
            New DrivingBinding With {.Action = "Steer Left", .Device = "Logitech G HUB G920 Driving Force Racing Wheel USB", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_x_axis", .Calibration = "biDirectionalLower"},
            New DrivingBinding With {.Action = "Steer Right", .Device = "Logitech G HUB G920 Driving Force Racing Wheel USB", .DeviceId = WheelId, .DeviceType = WheelProduct, .Input = "di_x_axis", .Calibration = "biDirectionalUpper"}}}
        Using rotationForm As New WheelRotationForm(steering)
            Dim hardware = DirectCast(rotationForm.Controls.Find("WheelRotationHardware", True).Single(), NumericUpDown)
            Dim inGame = DirectCast(rotationForm.Controls.Find("WheelRotationGame", True).Single(), NumericUpDown)
            check(hardware.Value = 900 AndAlso inGame.Value = 900 AndAlso inGame.Maximum = 900, "the G920 starts at its 900°, and DiRT 3's rotation at the current saturation")
            inGame.Value = 540
            check(rotationForm.Controls.Find("WheelRotationResult", True).Single().Text.Contains("540°"), "the result line follows the rotation")
            rotationForm.ApplyRotation()
            check(steering.Bindings.All(Function(b) b.Saturation = 0.6D) AndAlso steering.WheelRotation = 900 AndAlso rotationForm.DialogResult = DialogResult.OK, "Apply sets the steering saturation")
        End Using
        Using rotationForm As New WheelRotationForm(steering)
            Dim hardware = DirectCast(rotationForm.Controls.Find("WheelRotationHardware", True).Single(), NumericUpDown)
            check(hardware.Value = 900 AndAlso DirectCast(rotationForm.Controls.Find("WheelRotationGame", True).Single(), NumericUpDown).Value = 540, "it reopens with the saved rotations")
            hardware.Value = 360
            check(DirectCast(rotationForm.Controls.Find("WheelRotationGame", True).Single(), NumericUpDown).Value <= 360, "DiRT 3's rotation can't exceed the wheel's")
        End Using
    End Sub
End Module
