Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module DrivingInputTests
    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim wheel As New DrivingInput.Device(0, "{11111111-1111-1111-1111-111111111111}", "Test wheel", "{22222222-2222-2222-2222-222222222222}")
        Dim pad As New DrivingInput.Device(1, "xinput:0", "Xbox controller 1", "xinput_pad")
        check(pad.Xbox AndAlso Not wheel.Xbox, "XInput pads are recognized by their product id")
        check(DrivingInput.FeedbackMessage(pad, 1, 0, 0, Nothing) = "Rumbling Xbox controller 1 for one second.", "rumble test message")
        check(DrivingInput.FeedbackMessage(wheel, 1, 0, 0, {&H80070057, 0}) = "Playing a force-feedback shake on Test wheel for one second. (using sine on X)", "a fallback effect shape is named when it started")
        check(DrivingInput.FeedbackMessage(wheel, -1, 1, 1, {}) = "Test wheel reports no force feedback. (GetCapabilities returned 0x00000001)", "no-FFB message names the refusing call")
        check(DrivingInput.FeedbackMessage(wheel, -2, 5, &H80040205, {}).StartsWith("Test wheel is in use by another program. Close DiRT 3", StringComparison.Ordinal), "busy wheel message tells the player to close DiRT 3")
        check(DrivingInput.FeedbackMessage(wheel, -3, 6, &H80070057, {&H80070057, &H80070057, &H80004005}) = "Windows could not start force feedback on Test wheel. (CreateEffect: sine 0x80070057, sine on X 0x80070057, constant force 0x80004005)", "every refused effect shape is reported")
        check(DrivingInput.FeedbackMessage(wheel, 0, 99, 0, {}) = "The feedback test is unavailable for Test wheel.", "unknown results don't index past the step names")

        Dim sample = DrivingInput.Sample.Empty()
        check(DrivingInput.ButtonsText(wheel, sample) = "Disconnected" AndAlso DrivingInput.HatsText(sample) = "Disconnected", "disconnected devices say so")
        sample.Connected = 1 : sample.Buttons(0) = 1 : sample.Buttons(17) = 1 : sample.Pov(1) = 9000
        check(DrivingInput.ButtonsText(wheel, sample) = "0, 17", "wheel buttons are listed from 0")
        check(DrivingInput.HatsText(sample) = "0: center   1: 90°   2: center   3: center", "hats show center or degrees")
        Dim padSample = DrivingInput.Sample.Empty()
        padSample.Connected = 1 : padSample.Buttons(12) = 1 : padSample.Buttons(10) = 1 : padSample.Buttons(4) = 1
        check(DrivingInput.ButtonsText(pad, padSample) = "Start, A", "Xbox buttons use their names and skip unused bits")
        check(DrivingInput.PovNeutral(UInteger.MaxValue) AndAlso DrivingInput.PovNeutral(&HFFFFUI) AndAlso Not DrivingInput.PovNeutral(0), "neutral hat values are recognized")

        Dim missing = Path.Combine(root, "no-driving-input.dll")
        Dim refused = False
        Try
            Using reader As New DrivingInput(missing, IntPtr.Zero)
            End Using
        Catch ex As IOException When ex.Message.Contains("not installed")
            refused = True
        End Try
        check(refused, "a missing controller reader is reported, not a crash")

        Dim package = Path.Combine(root, "live-input-package")
        Dim ctx As New InstallContext(Path.Combine(package, "game"), Path.Combine(root, "live-input-users"), Path.Combine(package, "graphics.xml"), package)
        check(DrivingInput.LibraryPath(ctx) = Path.Combine(AppContext.BaseDirectory, "driving_input.dll"), "development builds look beside the executable")
        Directory.CreateDirectory(Path.Combine(package, "payload"))
        File.WriteAllText(Path.Combine(package, "payload", "driving_input.dll"), "placeholder")
        check(DrivingInput.LibraryPath(ctx) = Path.Combine(package, "payload", "driving_input.dll"), "packages use payload\driving_input.dll")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim library = Path.Combine(AppContext.BaseDirectory, "driving_input.dll")
        Dim installed = File.Exists(library)
        For Each candidate In {library, Path.Combine(root, "no-driving-input.dll")}
            Using monitor As New DrivingInputMonitor(candidate)
                monitor.ShowInTaskbar = False : monitor.StartPosition = FormStartPosition.Manual : monitor.Location = New Drawing.Point(-30000, -30000)
                monitor.Show() : Application.DoEvents()
                Dim status = DirectCast(monitor.Controls.Find("LiveInputStatus", True).Single(), Label).Text
                If candidate = library AndAlso installed Then
                    check(status.StartsWith("No controllers detected", StringComparison.Ordinal) OrElse status.StartsWith("Live — ", StringComparison.Ordinal), "live input opens with the controller reader (" & status & ")")
                    check(monitor.Controls.Find("LiveInputFeedbackTest", True).Length = monitor.DeviceCount, "every detected device has a feedback test")
                    monitor.RefreshDevices()
                    check(DirectCast(monitor.Controls.Find("LiveInputStatus", True).Single(), Label).Text.Length > 0, "refreshing devices keeps the window working")
                Else
                    check(status.Contains("not installed") AndAlso monitor.DeviceCount = 0, "live input explains a missing controller reader")
                End If
                monitor.Close()
            End Using
        Next
        Console.WriteLine("Note: live input " & If(installed, "tested with", "tested without") & " driving_input.dll")
    End Sub
End Module
