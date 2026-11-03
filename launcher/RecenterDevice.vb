Imports System.Runtime.InteropServices
Imports System.Text.RegularExpressions
Imports System.Windows.Forms

' A controller, wheel or button-box button (or hat direction) that recenters the VR view, as a
' second trigger beside the recenter key. Device identities are machine-specific, so this lives
' in launcher settings only, never in settings profiles. Adapted from GRID2VR's DeviceRecenter.
Public NotInheritable Class RecenterDeviceBinding
    Public Property DeviceId As String = ""
    Public Property DeviceName As String = ""
    Public Property Product As String = ""
    ' "button:N" (DirectInput 0-127, XInput 0-15) or "hat:N:up|right|down|left" (DirectInput only).
    Public Property Input As String = ""

    Private Shared ReadOnly Directions As String() = {"up", "right", "down", "left"}

    Public ReadOnly Property Xbox As Boolean
        Get
            Return Product = "xinput_pad"
        End Get
    End Property

    ' purpose names the binding in the message ("recenter", "seat menu").
    Public Sub Validate(Optional purpose As String = "recenter")
        Dim invalid = "The controller or wheel " & purpose & " binding is invalid. Bind the button again."
        Dim parsed As Guid
        Dim device = If(Xbox, Regex.IsMatch(If(DeviceId, ""), "\Axinput:[0-3]\z"), If(DeviceId, "").Length = 38 AndAlso Guid.TryParseExact(DeviceId, "B", parsed) AndAlso If(Product, "").Length = 38 AndAlso Guid.TryParseExact(Product, "B", parsed))
        If Not device OrElse DeviceName Is Nothing OrElse DeviceName.Length > 260 Then Throw New IOException(invalid)
        Dim button = Regex.Match(If(Input, ""), "\Abutton:(?<index>[0-9]{1,3})\z")
        If button.Success Then
            Dim index = Integer.Parse(button.Groups("index").Value, Globalization.CultureInfo.InvariantCulture)
            If If(Xbox, index < 16 AndAlso DrivingInput.XboxButtonNames(index).Length > 0, index < 128) Then Return
        ElseIf Not Xbox AndAlso Regex.IsMatch(If(Input, ""), "\Ahat:[0-3]:(up|right|down|left)\z") Then
            Return
        End If
        Throw New IOException(invalid)
    End Sub

    Public Function Describe() As String
        Return Control() & " on " & DeviceName
    End Function

    ' The button or hat direction alone, e.g. "Y" or "Button 5".
    Public Function Control() As String
        Dim parts = Input.Split(":"c)
        Return If(parts(0) = "hat", "Hat " & parts(1) & " " & parts(2),
                  If(Xbox, DrivingInput.XboxButtonNames(Integer.Parse(parts(1), Globalization.CultureInfo.InvariantCulture)), "Button " & parts(1)))
    End Function

    ' Whether two bindings are the same button or hat direction on the same device.
    Public Function SameInput(other As RecenterDeviceBinding) As Boolean
        Return other IsNot Nothing AndAlso String.Equals(DeviceId, other.DeviceId, StringComparison.OrdinalIgnoreCase) AndAlso
               String.Equals(Product, other.Product, StringComparison.OrdinalIgnoreCase) AndAlso Input = other.Input
    End Function

    ' Whether the bound button or hat direction is held in this sample.
    Public Function Down(sample As DrivingInput.Sample) As Boolean
        If sample.Connected = 0UI Then Return False
        Dim parts = Input.Split(":"c)
        Dim index = Integer.Parse(parts(1), Globalization.CultureInfo.InvariantCulture)
        If parts(0) = "button" Then Return sample.Buttons(index) <> 0
        Return PovDirection(sample.Pov(index)) = Array.IndexOf(Directions, parts(2))
    End Function

    ' 0 up, 1 right, 2 down, 3 left; -1 for centered or a diagonal, so one gesture can't mean two directions.
    Public Shared Function PovDirection(value As UInteger) As Integer
        If DrivingInput.PovNeutral(value) Then Return -1
        If value <= 2250UI OrElse (value >= 33750UI AndAlso value < 36000UI) Then Return 0
        If value >= 6750UI AndAlso value <= 11250UI Then Return 1
        If value >= 15750UI AndAlso value <= 20250UI Then Return 2
        If value >= 24750UI AndAlso value <= 29250UI Then Return 3
        Return -1
    End Function

    ' A button or hat direction pressed since the baseline sample; Nothing if none.
    Public Shared Function Detect(device As DrivingInput.Device, baseline As DrivingInput.Sample, current As DrivingInput.Sample) As RecenterDeviceBinding
        If baseline.Connected = 0UI OrElse current.Connected = 0UI Then Return Nothing
        Dim result As New RecenterDeviceBinding With {.DeviceId = device.Id, .DeviceName = device.Name, .Product = device.Product}
        For i = 0 To If(device.Xbox, 15, 127)
            If current.Buttons(i) = 0 OrElse baseline.Buttons(i) <> 0 Then Continue For
            If device.Xbox AndAlso DrivingInput.XboxButtonNames(i).Length = 0 Then Continue For
            result.Input = "button:" & i.ToString(Globalization.CultureInfo.InvariantCulture)
            Return result
        Next
        If Not device.Xbox Then
            For i = 0 To 3
                Dim direction = PovDirection(current.Pov(i))
                If direction < 0 OrElse direction = PovDirection(baseline.Pov(i)) Then Continue For
                result.Input = "hat:" & i.ToString(Globalization.CultureInfo.InvariantCulture) & ":" & Directions(direction)
                Return result
            Next
        End If
        Return Nothing
    End Function
End Class

' Fires once per press, only after the button was seen released while DiRT 3 was in front and the
' device connected, so a button already held when the game gains focus never recenters.
Public NotInheritable Class RecenterEdge
    Private armed As Boolean

    Public Function Update(connected As Boolean, active As Boolean, down As Boolean) As Boolean
        If Not connected OrElse Not active Then
            armed = False
            Return False
        End If
        If Not down Then
            armed = True
            Return False
        End If
        If Not armed Then Return False
        armed = False
        Return True
    End Function
End Class

' Watches the bound device during a VR session and asks the VR renderer to recenter by creating
' its request file. Reads without taking the device over; never sends input to the game.
Public NotInheritable Class RecenterDeviceWatcher
    Implements IDisposable

    Private ReadOnly binding As RecenterDeviceBinding
    Private ReadOnly requestPath As String
    Private ReadOnly readSample As Func(Of DrivingInput.Sample)
    Private ReadOnly isForeground As Func(Of Boolean)
    Private ReadOnly edge As New RecenterEdge()
    Private ReadOnly cleanup As Action
    Private pollTimer As Timer
    Public ReadOnly Property Requests As Integer
    Public ReadOnly Property ConnectedPolls As Integer
    Public Property Result As String = "running"

    Public Sub New(binding As RecenterDeviceBinding, requestPath As String, readSample As Func(Of DrivingInput.Sample), isForeground As Func(Of Boolean), Optional cleanup As Action = Nothing)
        binding.Validate()
        If Not Path.IsPathFullyQualified(requestPath) Then Throw New IOException("The recenter request path must be absolute.")
        Me.binding = binding : Me.requestPath = requestPath : Me.readSample = readSample : Me.isForeground = isForeground : Me.cleanup = cleanup
    End Sub

    ' One poll; True when a recenter request was written.
    Public Function Tick() As Boolean
        Dim sample = readSample()
        Dim connected = sample.Connected <> 0UI
        If connected Then _ConnectedPolls += 1
        If Not edge.Update(connected, isForeground(), binding.Down(sample)) Then Return False
        Try
            Using marker As New FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            End Using
            _Requests += 1
            Return True
        Catch ex As IOException When File.Exists(requestPath)
            Return False ' The renderer has not taken the previous request yet.
        End Try
    End Function

    Public Sub Start(Optional interval As Integer = 25)
        pollTimer = New Timer With {.Interval = interval}
        AddHandler pollTimer.Tick, Sub()
                                   Try
                                       Tick()
                                   Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ObjectDisposedException OrElse
                                                              TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
                                       Result = ex.Message : pollTimer?.Stop()
                                   End Try
                               End Sub
        pollTimer.Start()
    End Sub

    <DllImport("user32.dll")> Private Shared Function GetForegroundWindow() As IntPtr
    End Function
    <DllImport("user32.dll")> Private Shared Function GetWindowThreadProcessId(window As IntPtr, ByRef process As UInteger) As UInteger
    End Function

    ' Whether the game's window is the foreground window.
    Public Shared Function InFront(gameProcessId As Integer) As Boolean
        Dim owner As UInteger
        GetWindowThreadProcessId(GetForegroundWindow(), owner)
        Return owner = CUInt(gameProcessId)
    End Function

    ' Watches the real device through driving_input.dll, re-finding it every two seconds if it is unplugged.
    Public Shared Function ForSession(libraryPath As String, window As IntPtr, binding As RecenterDeviceBinding, gameProcessId As Integer, requestPath As String) As RecenterDeviceWatcher
        Dim input As DrivingInput = Nothing, device As DrivingInput.Device = Nothing
        Dim refreshAt As Long = 0
        Dim read = Function() As DrivingInput.Sample
                       Dim now = Environment.TickCount64
                       If device Is Nothing AndAlso now >= refreshAt Then
                           refreshAt = now + 2000
                           input?.Dispose() : input = Nothing
                           input = New DrivingInput(libraryPath, window)
                           device = input.Devices.FirstOrDefault(Function(d) String.Equals(d.Id, binding.DeviceId, StringComparison.OrdinalIgnoreCase) AndAlso String.Equals(d.Product, binding.Product, StringComparison.OrdinalIgnoreCase))
                       End If
                       Dim sample = If(device Is Nothing, DrivingInput.Sample.Empty(), input.Read(device))
                       If sample.Connected = 0UI Then device = Nothing
                       Return sample
                   End Function
        Return New RecenterDeviceWatcher(binding, requestPath, read, Function() InFront(gameProcessId), Sub() input?.Dispose())
    End Function

    ' Stops watching and records what happened in the session logs.
    Public Sub Dispose() Implements IDisposable.Dispose
        If pollTimer IsNot Nothing Then pollTimer.Stop() : pollTimer.Dispose() : pollTimer = Nothing
        cleanup?.Invoke()
        If Result = "running" Then Result = "stopped"
        Try
            Dim folder = Path.GetDirectoryName(requestPath)
            If Directory.Exists(folder) Then SafeFiles.SaveJson(Path.Combine(folder, "recenter-controller.json"), New With {.schema = 1, .binding = binding.Describe(), .requests = Requests, .connectedPolls = ConnectedPolls, .result = Result})
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
    End Sub
End Class

' "Press the button": takes the first fresh button press or hat direction on any connected device.
Public NotInheritable Class RecenterDeviceForm
    Inherits LauncherForm

    Private ReadOnly libraryPath As String
    Private ReadOnly status As New Label With {.Name = "RecenterDeviceStatus", .AutoSize = True, .MaximumSize = New Drawing.Size(460, 0), .UseMnemonic = False, .Margin = New Padding(3, 12, 3, 12)}
    Private ReadOnly pollTimer As New Timer With {.Interval = 30}
    Private ReadOnly previous As New Dictionary(Of String, DrivingInput.Sample)(StringComparer.OrdinalIgnoreCase)
    Private input As DrivingInput
    Public ReadOnly Property Binding As RecenterDeviceBinding

    Public Sub New(libraryPath As String, Optional title As String = "Recenter button",
                   Optional prompt As String = "Press the button, or the D-pad / hat direction, on your controller, wheel or button box that should recenter the view.")
        Me.libraryPath = libraryPath
        Text = title
        Font = New Drawing.Font("Segoe UI", 10) : AutoScaleMode = AutoScaleMode.Dpi
        AutoSize = True : AutoSizeMode = AutoSizeMode.GrowAndShrink
        FormBorderStyle = FormBorderStyle.FixedDialog : StartPosition = FormStartPosition.CenterParent : MinimizeBox = False : MaximizeBox = False
        Dim layout As New TableLayoutPanel With {.AutoSize = True, .Padding = New Padding(20), .ColumnCount = 1}
        layout.Controls.Add(New Label With {.Text = prompt, .AutoSize = True, .MaximumSize = New Drawing.Size(460, 0)})
        status.Text = "Looking for controllers…"
        layout.Controls.Add(status)
        Dim cancel As New Button With {.Name = "CancelRecenterDevice", .Text = "Cancel", .AutoSize = True, .DialogResult = DialogResult.Cancel, .Anchor = AnchorStyles.Right}
        layout.Controls.Add(cancel)
        Controls.Add(layout)
        CancelButton = cancel
        AddHandler pollTimer.Tick, Sub() Poll()
        AddHandler Shown, Sub() Open()
    End Sub

    Public ReadOnly Property StatusText As String
        Get
            Return status.Text
        End Get
    End Property

    Private Sub Open()
        Try
            input = New DrivingInput(libraryPath, Handle)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
            status.Text = ex.Message
            Return
        End Try
        status.Text = If(input.Devices.Count = 0, "No controllers detected. Connect one, then open this again.", "Waiting for a button on: " & String.Join(", ", input.Devices.Select(Function(d) d.Name)))
        If input.Devices.Count > 0 Then pollTimer.Start()
    End Sub

    Private Sub Poll()
        If input Is Nothing Then Return
        For Each device In input.Devices
            Dim sample = input.Read(device)
            Dim before As DrivingInput.Sample = Nothing
            If previous.TryGetValue(device.Id, before) Then
                Dim found = RecenterDeviceBinding.Detect(device, before, sample)
                If found IsNot Nothing Then
                    pollTimer.Stop()
                    _Binding = found
                    DialogResult = DialogResult.OK
                    Close()
                    Return
                End If
            End If
            previous(device.Id) = sample
        Next
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            pollTimer.Stop() : pollTimer.Dispose()
            input?.Dispose() : input = Nothing
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
