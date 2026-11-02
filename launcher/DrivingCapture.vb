Imports System.Globalization
Imports System.Windows.Forms

' Learns a stable rest, then requires a deliberate movement and its release, so a held switch,
' a noisy unused pedal, lost focus or a disconnect can't assign a stale gesture. Callers pass
' monotonic milliseconds, so timing is testable. Ported from GRID2VR's launcher/DrivingInput.cs
' (Detect) and DrivingCapture, with DiRT 3's input and calibrator names.
Public NotInheritable Class DrivingCapture
    Private Shared ReadOnly HatDirections As String() = {"up", "right", "down", "left"}

    Private ReadOnly device As DrivingInput.Device
    Private ReadOnly actionName As String
    Private baseline As DrivingInput.Sample
    Private initialized As Boolean
    Private presentAxes As UInteger
    Private ReadOnly axisSince(7) As Long
    Private candidateSince As Long
    Private releaseSince As Long = -1
    Private candidate As DrivingBinding

    Public Property AxesOnly As Boolean
    Public Property ButtonsOnly As Boolean
    Public ReadOnly Property Ready As Boolean
    Public ReadOnly Property WaitingForRelease As Boolean
    Public ReadOnly Property Completed As DrivingBinding

    Public Sub New(value As DrivingInput.Device, action As String)
        device = value : actionName = action
    End Sub

    Public ReadOnly Property AxesReady As Boolean
        Get
            Return initialized AndAlso Not ButtonsOnly AndAlso baseline.Axes <> 0UI
        End Get
    End Property

    Public ReadOnly Property Detected As DrivingBinding
        Get
            Return candidate
        End Get
    End Property

    Public Shared Function Clone(value As DrivingInput.Sample) As DrivingInput.Sample
        value.Values = CType(value.Values.Clone(), Single())
        value.Buttons = CType(value.Buttons.Clone(), Byte())
        value.Pov = CType(value.Pov.Clone(), UInteger())
        Return value
    End Function

    Public Shared Function IsAxis(binding As DrivingBinding) As Boolean
        Return DrivingControls.DirectAxes.Contains(binding.Input) OrElse DrivingControls.XboxAxes.Contains(binding.Input)
    End Function

    ' A deliberate digital press wins over unrelated pedal or wheel movement in the same poll.
    Public Shared Function ChooseActive(captures As IEnumerable(Of DrivingCapture), current As DrivingCapture) As DrivingCapture
        Dim digital = Function(capture As DrivingCapture) capture.WaitingForRelease AndAlso capture.Detected IsNot Nothing AndAlso Not IsAxis(capture.Detected)
        If current IsNot Nothing AndAlso digital(current) Then Return current
        Dim pressed = captures.FirstOrDefault(Function(capture) digital(capture))
        If pressed IsNot Nothing Then Return pressed
        If current IsNot Nothing AndAlso current.WaitingForRelease Then Return current
        Return captures.FirstOrDefault(Function(capture) capture.WaitingForRelease)
    End Function

    ' The input that changed from baseline to current, as a binding for the action, or Nothing.
    Public Shared Function Detect(action As String, value As DrivingInput.Device, rest As DrivingInput.Sample, current As DrivingInput.Sample,
                                  Optional axesOnly As Boolean = False, Optional buttonsOnly As Boolean = False) As DrivingBinding
        If rest.Connected = 0UI OrElse current.Connected = 0UI Then Return Nothing
        Dim result = Function(input As String) New DrivingBinding With {.Action = action, .DeviceId = value.Id, .Device = value.Name, .DeviceType = value.Product, .Input = input}
        ' A fresh button or hat press is more specific than incidental axis travel, so
        ' Automatic can't let a moving pedal hide a button press.
        If Not axesOnly Then
            For i = 0 To 127
                If current.Buttons(i) = 0 OrElse rest.Buttons(i) <> 0 Then Continue For
                If value.Xbox AndAlso (i >= DrivingControls.XboxButtons.Length OrElse DrivingControls.XboxButtons(i).Length = 0) Then Continue For
                Return result(If(value.Xbox, DrivingControls.XboxButtons(i), "di_button_" & i.ToString(CultureInfo.InvariantCulture)))
            Next
            If Not value.Xbox Then
                For i = 0 To 3
                    Dim direction = RecenterDeviceBinding.PovDirection(current.Pov(i))
                    If DrivingInput.PovNeutral(rest.Pov(i)) AndAlso direction >= 0 Then Return result("di_dpad_" & i.ToString(CultureInfo.InvariantCulture) & "_" & HatDirections(direction))
                Next
            End If
        End If
        If buttonsOnly Then Return Nothing
        Dim best = -1, movement = 0.0F
        Dim steering = action.StartsWith("Steer ", StringComparison.Ordinal)
        For i = 0 To 7
            If (current.Axes And rest.Axes And (1UI << i)) = 0UI Then Continue For
            Dim delta = Math.Abs(current.Values(i) - rest.Values(i))
            Dim centeredAxis = Math.Abs(rest.Values(i)) < 0.35F
            If steering AndAlso (Not centeredAxis OrElse (value.Xbox AndAlso i >= 4)) Then Continue For
            ' Wheel rotation needs less travel than a pedal; end-resting pedals span two units.
            Dim threshold = If(steering AndAlso Not value.Xbox, 0.15F, If(centeredAxis OrElse value.Xbox, 0.55F, 0.9F))
            If Single.IsFinite(delta) AndAlso delta >= threshold AndAlso delta > movement Then best = i : movement = delta
        Next
        If best < 0 OrElse (value.Xbox AndAlso best >= 6) Then Return Nothing
        Dim centered = steering OrElse Math.Abs(rest.Values(best)) < 0.35F
        Dim negative = current.Values(best) < rest.Values(best)
        Dim valid = True
        If steering Then valid = Math.Abs(current.Values(best)) >= 0.15F : negative = current.Values(best) < 0
        If value.Xbox Then
            If best >= 4 Then
                ' XInput triggers rest at zero; never bind a trigger's release as inverted.
                valid = valid AndAlso rest.Values(best) <= 0.15F AndAlso current.Values(best) >= 0.4F : centered = False : negative = False
            Else
                ' XInput Y is up-positive, as DiRT 3's stock Look Up (biDirectionalUpper) expects.
                valid = valid AndAlso Math.Abs(rest.Values(best)) <= 0.25F : centered = True : negative = current.Values(best) < 0
            End If
        End If
        If Not valid Then Return Nothing
        Dim found = result(If(value.Xbox, DrivingControls.XboxAxes, DrivingControls.DirectAxes)(best))
        found.Calibration = If(centered, If(negative, "biDirectionalLower", "biDirectionalUpper"), If(negative, "uniDirectionalNegative", "uniDirectionalPositive"))
        Return found
    End Function

    Public Shared Function Description(binding As DrivingBinding) As String
        If binding Is Nothing Then Return "DiRT 3's binding"
        Return If(binding.Keyboard, InputDescription(binding), binding.Device & " · " & InputDescription(binding))
    End Function

    Public Shared Function InputDescription(binding As DrivingBinding) As String
        If binding.Keyboard Then
            Dim code = Convert.ToInt32(binding.Input.Substring(10), 16)
            Return If(New KeysConverter().ConvertToString(CType(code, Keys)), binding.Input)
        End If
        Dim direction = If(binding.Calibration = "biDirectionalLower", " (−)", If(binding.Calibration = "biDirectionalUpper", " (+)", If(binding.Calibration = "uniDirectionalNegative", " (inverted)", "")))
        Return binding.Input.Replace("xinput_", "").Replace("di_", "").Replace("_"c, " "c) & direction
    End Function

    Public Sub Reset()
        initialized = False : _Ready = False : _WaitingForRelease = False : _Completed = Nothing : candidate = Nothing : releaseSince = -1
    End Sub

    Public Sub Update(current As DrivingInput.Sample, now As Long)
        If current.Connected = 0UI Then Reset() : Return
        If _Completed IsNot Nothing Then Return
        If Not initialized Then
            ' Released buttons are armed at once; inputs already held stay blocked until released.
            baseline = Clone(current) : presentAxes = current.Axes : baseline.Axes = 0UI
            Array.Fill(axisSince, now) : initialized = True : _Ready = Not AxesOnly
            Return
        End If
        If presentAxes <> current.Axes Then Reset() : Return
        For i = 0 To 7
            Dim bit = 1UI << i
            If ButtonsOnly OrElse (presentAxes And bit) = 0UI OrElse (baseline.Axes And bit) <> 0UI Then Continue For
            Dim shifted = Not Single.IsFinite(baseline.Values(i)) OrElse Not Single.IsFinite(current.Values(i)) OrElse Math.Abs(current.Values(i) - baseline.Values(i)) > 0.06F
            If device.Xbox AndAlso i < 6 AndAlso Math.Abs(current.Values(i)) > If(i < 4, 0.25F, 0.15F) Then shifted = True
            If shifted Then baseline.Values(i) = current.Values(i) : axisSince(i) = now
            If Single.IsFinite(current.Values(i)) AndAlso now - axisSince(i) >= 600 Then baseline.Axes = baseline.Axes Or bit
        Next
        _Ready = (Not ButtonsOnly AndAlso baseline.Axes <> 0UI) OrElse Not AxesOnly
        If Not _Ready Then Return
        For i = 0 To 127
            If current.Buttons(i) = 0 Then baseline.Buttons(i) = 0
        Next
        For i = 0 To 3
            If DrivingInput.PovNeutral(current.Pov(i)) Then baseline.Pov(i) = UInteger.MaxValue
        Next
        If _WaitingForRelease AndAlso Not AxesOnly AndAlso candidate IsNot Nothing AndAlso IsAxis(candidate) Then
            ' An axis can stay displaced on a noisy wheel or pedal set; a later deliberate
            ' button press must still work in Automatic.
            Dim digital = Detect(actionName, device, baseline, current, buttonsOnly:=True)
            If digital IsNot Nothing Then candidate = digital : releaseSince = -1
        End If
        If Not _WaitingForRelease Then
            Dim found = Detect(actionName, device, baseline, current, AxesOnly, ButtonsOnly)
            If found Is Nothing Then candidate = Nothing : Return
            If Not found.SameAs(candidate) Then candidate = found : candidateSince = now
            If IsAxis(candidate) AndAlso now - candidateSince < 100 Then Return
            _WaitingForRelease = True
        End If
        Dim axis = Array.IndexOf(If(device.Xbox, DrivingControls.XboxAxes, DrivingControls.DirectAxes), candidate.Input)
        Dim released As Boolean
        If axis >= 0 Then
            released = Math.Abs(current.Values(axis) - baseline.Values(axis)) < 0.12F
        ElseIf candidate.Input.StartsWith("di_dpad_", StringComparison.Ordinal) Then
            released = DrivingInput.PovNeutral(current.Pov(AscW(candidate.Input(8)) - AscW("0"c)))
        Else
            Dim button = If(device.Xbox, Array.IndexOf(DrivingControls.XboxButtons, candidate.Input), Integer.Parse(candidate.Input.Substring(10), CultureInfo.InvariantCulture))
            released = button >= 0 AndAlso current.Buttons(button) = 0
        End If
        If Not released Then releaseSince = -1 : Return
        If releaseSince < 0 Then releaseSince = now
        If now - releaseSince >= 160 Then _Completed = candidate
    End Sub
End Class
