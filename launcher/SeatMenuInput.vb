Imports System.Windows.Forms

' Controller input for the in-game seat menu (#c137c681; docs/seat-channel.md held bits; DiRT2VR's
' SeatChannel.Poll, simplified). The launcher reads controllers without taking them over, as for the
' recenter button, and publishes what the menu should see; the renderer turns it into presses and keeps
' those buttons from DiRT 3 while the menu is open. Keyboard keys are read by the renderer itself.
Public NotInheritable Class SeatMenuInput
    Public Const SidewaysBit As Integer = 6
    Public Const UpBit As Integer = 7, DownBit As Integer = 8, LeftBit As Integer = 9, RightBit As Integer = 10
    Public Const SaveBit As Integer = 11, CancelBit As Integer = 12, OpenBit As Integer = 13
    ' XInput buttons by DrivingInput index (= wButtons bit): D-pad up, down, left, right, A, B, X.
    Private Shared ReadOnly XboxMenuButtons As Integer() = {0, 1, 2, 3, 12, 13, 14}
    Private Shared ReadOnly XboxMenuBits As Integer() = {UpBit, DownBit, LeftBit, RightBit, SaveBit, CancelBit, SidewaysBit}
    ' RecenterDeviceBinding.PovDirection (0 up, 1 right, 2 down, 3 left) → menu bits.
    Private Shared ReadOnly HatBits As Integer() = {UpBit, RightBit, DownBit, LeftBit}
    Public Const XboxHint As String = "Arrows / D-pad: move. Hold Shift / X: sideways. Enter / A: save. Esc / B: cancel."

    Private Sub New()
    End Sub

    ' What one device gives the menu: the held bits, the buttons the menu uses on it (kept from DiRT 3 while
    ' the menu is open; XInput: a wButtons mask in [0]; DirectInput: bit n = button n) and whether it uses its hat.
    Public Structure Contribution
        Public Held As UInteger
        Public Buttons As UInteger()
        Public Hat As Boolean

        Public Shared Function None() As Contribution
            Return New Contribution With {.Buttons = New UInteger(3) {}}
        End Function
    End Structure

    ' Whether the bound button is on this device.
    Public Shared Function Owns(open As RecenterDeviceBinding, device As DrivingInput.Device) As Boolean
        Return open IsNot Nothing AndAlso device IsNot Nothing AndAlso String.Equals(open.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase) AndAlso
               String.Equals(open.Product, device.Product, StringComparison.OrdinalIgnoreCase)
    End Function

    ' One device's contribution. open: the bound menu button (Nothing for none); menuOpen: what the renderer shows.
    ' Closed, only the bound button counts; open, an Xbox pad's D-pad, A, B and X and a wheel's first hat do too.
    Public Shared Function Read(device As DrivingInput.Device, sample As DrivingInput.Sample, open As RecenterDeviceBinding, menuOpen As Boolean) As Contribution
        Dim result = Contribution.None()
        If sample.Connected = 0UI Then Return result
        If Owns(open, device) Then
            Block(result, open)
            If open.Down(sample) Then result.Held = result.Held Or (1UI << OpenBit)
        End If
        If Not menuOpen Then Return result
        If device.Xbox Then
            For i = 0 To XboxMenuButtons.Length - 1
                If sample.Buttons(XboxMenuButtons(i)) <> 0 Then result.Held = result.Held Or (1UI << XboxMenuBits(i))
                result.Buttons(0) = result.Buttons(0) Or (1UI << XboxMenuButtons(i))
            Next
        Else
            Dim direction = RecenterDeviceBinding.PovDirection(sample.Pov(0))
            If direction >= 0 Then result.Held = result.Held Or (1UI << HatBits(direction))
            result.Hat = True
        End If
        Return result
    End Function

    Private Shared Sub Block(ByRef result As Contribution, open As RecenterDeviceBinding)
        Dim parts = open.Input.Split(":"c)
        If parts(0) = "hat" Then
            result.Hat = True
            Return
        End If
        Dim index = Integer.Parse(parts(1), Globalization.CultureInfo.InvariantCulture)
        result.Buttons(index \ 32) = result.Buttons(index \ 32) Or (1UI << (index Mod 32))
    End Sub

    ' Why a button can't open the menu, or "" when it can: the menu's own buttons move, save and cancel.
    Public Shared Function Reserved(binding As RecenterDeviceBinding) As String
        Dim parts = binding.Input.Split(":"c)
        If binding.Xbox AndAlso parts(0) = "button" AndAlso XboxMenuButtons.Contains(Integer.Parse(parts(1), Globalization.CultureInfo.InvariantCulture)) Then
            Return "On an Xbox controller the D-pad, A, B and X move, save and cancel in the seat menu. Choose another button."
        End If
        If Not binding.Xbox AndAlso parts(0) = "hat" AndAlso parts(1) = "0" Then Return "The first hat moves the seat in the menu. Choose a button or another hat."
        Return ""
    End Function

    ' The device the menu listens to now: one holding something, else the bound button's device, else an Xbox pad.
    Public Shared Function Choose(devices As IEnumerable(Of (Device As DrivingInput.Device, Input As Contribution)), open As RecenterDeviceBinding) As (Device As DrivingInput.Device, Input As Contribution)
        Dim list = devices.ToList()
        Dim active = list.FirstOrDefault(Function(d) d.Input.Held <> 0UI)
        If active.Device IsNot Nothing Then Return active
        Dim bound = list.FirstOrDefault(Function(d) Owns(open, d.Device))
        If bound.Device IsNot Nothing Then Return bound
        Dim pad = list.FirstOrDefault(Function(d) d.Device.Xbox)
        If pad.Device IsNot Nothing Then Return pad
        Return (Nothing, Contribution.None())
    End Function

    ' The hint under the menu for the device in use.
    Public Shared Function Hint(device As DrivingInput.Device, open As RecenterDeviceBinding) As String
        If device Is Nothing OrElse device.Xbox Then Return XboxHint
        Return "Arrows / hat: move. Hold Shift: sideways. Enter" & If(Owns(open, device), " / " & open.Control(), "") & ": save. Esc: cancel."
    End Function

    ' Publishes a choice into the channel (source 1 XInput with its slot, 2 DirectInput with its GUID).
    Public Shared Sub Publish(channel As SeatChannel, chosen As (Device As DrivingInput.Device, Input As Contribution))
        If chosen.Device Is Nothing Then
            channel.WriteInput(0UI, 0, 0, Guid.Empty, New UInteger(3) {}, False)
            Return
        End If
        Dim slot = 0, id As Guid = Guid.Empty
        If chosen.Device.Xbox Then
            Integer.TryParse(chosen.Device.Id.Substring(chosen.Device.Id.IndexOf(":"c) + 1), slot)
        Else
            Guid.TryParse(chosen.Device.Id, id)
        End If
        channel.WriteInput(chosen.Input.Held, If(chosen.Device.Xbox, 1, 2), slot, id, If(chosen.Input.Buttons, New UInteger(3) {}), chosen.Input.Hat)
    End Sub
End Class

' What the menu sees from one poll to the next (DiRT2VR's Poll): nothing unless DiRT 3 is in front; a button
' already held when the menu opens or closes, or when DiRT 3 comes to the front, counts only once it is
' released (so the press that opened the menu doesn't also save it); and while the menu is open, the first
' device used keeps it until the menu closes.
Public NotInheritable Class SeatMenuTracker
    Private ReadOnly waiting As New Dictionary(Of String, UInteger)(StringComparer.OrdinalIgnoreCase)
    Private wasOpen As Boolean, wasFocused As Boolean
    Private selected As String

    Public Function Update(reads As IEnumerable(Of (Device As DrivingInput.Device, Input As SeatMenuInput.Contribution)), menuOpen As Boolean, focused As Boolean, open As RecenterDeviceBinding) As (Device As DrivingInput.Device, Input As SeatMenuInput.Contribution)
        Dim changed = menuOpen <> wasOpen OrElse focused <> wasFocused
        wasOpen = menuOpen : wasFocused = focused
        If Not menuOpen Then selected = Nothing
        Dim usable As New List(Of (Device As DrivingInput.Device, Input As SeatMenuInput.Contribution))
        For Each read In reads
            Dim held = read.Input.Held, stale As UInteger = 0UI
            If changed Then
                stale = held
            ElseIf waiting.TryGetValue(read.Device.Id, stale) Then
                stale = stale And held
            End If
            waiting(read.Device.Id) = stale
            Dim input = read.Input
            input.Held = held And Not stale
            usable.Add((read.Device, input))
        Next
        If Not focused Then Return (Nothing, SeatMenuInput.Contribution.None())
        If selected IsNot Nothing Then
            Dim kept = usable.FirstOrDefault(Function(d) String.Equals(d.Device.Id, selected, StringComparison.OrdinalIgnoreCase))
            If kept.Device IsNot Nothing Then Return kept
            selected = Nothing ' Unplugged: any device may take over.
        End If
        Dim chosen = SeatMenuInput.Choose(usable, open)
        If menuOpen AndAlso chosen.Device IsNot Nothing AndAlso chosen.Input.Held <> 0UI Then selected = chosen.Device.Id
        Return chosen
    End Function
End Class

' Reads controllers for the seat menu every 30 ms while a VR session runs: only the bound button's device
' while the menu is closed, every device while it is open. Devices are found again when the menu opens, and
' every two seconds while the bound one is missing, so a controller plugged in during the session works.
Public NotInheritable Class SeatMenuWatcher
    Implements IDisposable

    Private ReadOnly libraryPath As String
    Private ReadOnly window As IntPtr
    Private ReadOnly channel As SeatChannel
    Private ReadOnly open As RecenterDeviceBinding
    Private ReadOnly focused As Func(Of Boolean)
    Private ReadOnly logFolder As String
    Private ReadOnly tracker As New SeatMenuTracker()
    Private input As DrivingInput
    Private foundAt As Long
    Private wasOpen As Boolean
    Private pollTimer As Timer
    Public ReadOnly Property Polls As Integer
    Public ReadOnly Property Presses As Integer
    Public Property Result As String = "running"
    Private lastHeld As UInteger

    Public Sub New(libraryPath As String, window As IntPtr, seatChannel As SeatChannel, openButton As RecenterDeviceBinding, focused As Func(Of Boolean), Optional logFolder As String = Nothing)
        openButton?.Validate("seat menu")
        Me.libraryPath = libraryPath : Me.window = window : channel = seatChannel : open = openButton : Me.focused = focused : Me.logFolder = logFolder
        Find()
    End Sub

    Private Sub Find()
        input?.Dispose() : input = Nothing
        foundAt = Environment.TickCount64
        input = New DrivingInput(libraryPath, window)
    End Sub

    Public Sub Start(Optional interval As Integer = 30)
        pollTimer = New Timer With {.Interval = interval}
        AddHandler pollTimer.Tick, Sub()
                                       Try
                                           Tick()
                                       Catch ex As ObjectDisposedException
                                           Result = "session ended" : pollTimer?.Stop()
                                       Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                                                  TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
                                           Result = ex.Message : pollTimer?.Stop() : Release()
                                       End Try
                                   End Sub
        pollTimer.Start()
    End Sub

    ' One poll: reads what the menu needs now and publishes it with the matching hint.
    Public Sub Tick()
        Dim menuOpen = channel.MenuOpen
        Dim due = Environment.TickCount64 - foundAt >= 2000
        If due AndAlso ((menuOpen AndAlso Not wasOpen) OrElse (Not menuOpen AndAlso open IsNot Nothing AndAlso Not input.Devices.Any(Function(d) SeatMenuInput.Owns(open, d)))) Then Find()
        wasOpen = menuOpen
        Dim devices = If(menuOpen, input.Devices, input.Devices.Where(Function(d) SeatMenuInput.Owns(open, d)))
        Dim reads = devices.Select(Function(d) (Device:=d, Input:=SeatMenuInput.Read(d, input.Read(d), open, menuOpen))).ToList()
        Dim chosen = tracker.Update(reads, menuOpen, focused(), open)
        SeatMenuInput.Publish(channel, chosen)
        channel.WriteHint(SeatMenuInput.Hint(chosen.Device, open))
        Dim held = chosen.Input.Held
        If (held And Not lastHeld And (1UI << SeatMenuInput.OpenBit)) <> 0UI Then _Presses += 1
        lastHeld = held
        _Polls += 1
    End Sub

    ' Nothing held, so a watcher that stops never leaves the seat moving.
    Private Sub Release()
        Try
            channel.WriteInput(0UI, 0, 0, Guid.Empty, New UInteger(3) {}, False)
        Catch ex As ObjectDisposedException
        End Try
    End Sub

    ' Stops reading and records what happened in the session logs.
    Public Sub Dispose() Implements IDisposable.Dispose
        If pollTimer IsNot Nothing Then pollTimer.Stop() : pollTimer.Dispose() : pollTimer = Nothing
        Release()
        input?.Dispose() : input = Nothing
        If Result = "running" Then Result = "stopped"
        If logFolder Is Nothing Then Return
        Try
            If Directory.Exists(logFolder) Then SafeFiles.SaveJson(Path.Combine(logFolder, "seat-menu-controller.json"), New With {.schema = 1, .button = If(open Is Nothing, "", open.Describe()), .presses = Presses, .polls = Polls, .result = Result})
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
    End Sub
End Class
