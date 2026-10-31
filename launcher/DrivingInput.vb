Imports System.Runtime.InteropServices
Imports System.Text

' Reads wheels, pedals, shifters and controllers through payload\driving_input.dll
' (tools/driving-input, from GRID2VR). Reading is non-exclusive, so DiRT 3 keeps its
' own input; nothing is injected or synthesized. Only the explicit feedback test briefly
' takes a wheel exclusively. Ported from GRID2VR's launcher/DrivingInput.cs.
Public NotInheritable Class DrivingInput
    Implements IDisposable

    <StructLayout(LayoutKind.Sequential)>
    Public Structure Sample
        Public Connected As UInteger
        Public Axes As UInteger
        <MarshalAs(UnmanagedType.ByValArray, SizeConst:=8)> Public Values As Single()
        <MarshalAs(UnmanagedType.ByValArray, SizeConst:=128)> Public Buttons As Byte()
        <MarshalAs(UnmanagedType.ByValArray, SizeConst:=4)> Public Pov As UInteger()

        Public Shared Function Empty() As Sample
            Return New Sample With {.Values = New Single(7) {}, .Buttons = New Byte(127) {}, .Pov = {UInteger.MaxValue, UInteger.MaxValue, UInteger.MaxValue, UInteger.MaxValue}}
        End Function
    End Structure

    Public NotInheritable Class Device
        Public ReadOnly Property Index As UInteger
        Public ReadOnly Property Id As String
        Public ReadOnly Property Name As String
        Public ReadOnly Property Product As String

        Public Sub New(index As UInteger, id As String, name As String, product As String)
            Me.Index = index : Me.Id = id : Me.Name = name : Me.Product = product
        End Sub

        Public ReadOnly Property Xbox As Boolean
            Get
                Return Product = "xinput_pad"
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    ' XInput wButtons bit order; bits 10 and 11 are unused.
    Public Shared ReadOnly XboxButtonNames As String() = {"D-pad up", "D-pad down", "D-pad left", "D-pad right", "Start", "Back", "Left stick", "Right stick", "LB", "RB", "", "", "A", "B", "X", "Y"}
    Public Shared ReadOnly FeedbackSteps As String() = {"", "GetCapabilities", "CreateDevice", "SetDataFormat", "SetCooperativeLevel", "Acquire", "CreateEffect", "Start", "XInputSetState"}
    Public Shared ReadOnly FeedbackShapes As String() = {"sine", "sine on X", "constant force"}
    Public Const FeedbackStarted As Integer = 1

    <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function OpenCall(window As IntPtr) As IntPtr
    <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Sub CloseCall(handle As IntPtr)
    <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function CountCall(handle As IntPtr) As UInteger
    <UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet:=CharSet.Unicode)> Private Delegate Function NameCall(handle As IntPtr, index As UInteger, id As StringBuilder, idSize As UInteger, name As StringBuilder, nameSize As UInteger) As Integer
    <UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet:=CharSet.Unicode)> Private Delegate Function ProductCall(handle As IntPtr, index As UInteger, product As StringBuilder, size As UInteger) As Integer
    <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function ReadCall(handle As IntPtr, index As UInteger, ByRef sample As Sample) As Integer
    <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function FeedbackCall(handle As IntPtr, index As UInteger, milliseconds As UInteger) As Integer
    <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function FeedbackErrorCall(handle As IntPtr, <Out> ByRef result As Integer) As UInteger
    <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function FeedbackShapesCall(handle As IntPtr, <Out> results As Integer(), capacity As UInteger) As UInteger

    Private library, handle As IntPtr
    Private closeNative As CloseCall
    Private readNative As ReadCall
    Private feedbackNative As FeedbackCall
    Private feedbackErrorNative As FeedbackErrorCall
    Private feedbackShapesNative As FeedbackShapesCall
    Public ReadOnly Property Devices As New List(Of Device)

    ' The package keeps the reader in payload\; development and test builds keep it beside the executable.
    Public Shared Function LibraryPath(context As InstallContext) As String
        Dim packaged = Path.Combine(context.PackageRoot, "payload", "driving_input.dll")
        Return If(File.Exists(packaged), packaged, Path.Combine(AppContext.BaseDirectory, "driving_input.dll"))
    End Function

    Private Function Export(Of T)(name As String) As T
        Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(library, name))
    End Function

    Private Function OptionalExport(Of T)(name As String) As T
        Dim address As IntPtr
        Return If(NativeLibrary.TryGetExport(library, name, address), Marshal.GetDelegateForFunctionPointer(Of T)(address), Nothing)
    End Function

    Public Sub New(path As String, window As IntPtr)
        SafeFiles.NoLinks(path)
        If Not File.Exists(path) Then Throw New IOException("Controller reading is not installed (payload\driving_input.dll is missing). Reinstall DiRT3VR or rebuild it with tools\build.ps1.")
        Try
            library = NativeLibrary.Load(path)
            closeNative = Export(Of CloseCall)("CaptureClose") : readNative = Export(Of ReadCall)("CaptureRead")
            ' A reader without the feedback test still shows live input.
            feedbackNative = OptionalExport(Of FeedbackCall)("CaptureFeedback")
            feedbackErrorNative = OptionalExport(Of FeedbackErrorCall)("CaptureFeedbackError")
            feedbackShapesNative = OptionalExport(Of FeedbackShapesCall)("CaptureFeedbackShapes")
            handle = Export(Of OpenCall)("CaptureOpen")(window)
            If handle = IntPtr.Zero Then Throw New IOException("Windows controller reading could not start.")
            Dim count = Export(Of CountCall)("CaptureCount")(handle)
            If count > 256UI Then Throw New IOException("Unexpected controller count.")
            Dim describe = Export(Of NameCall)("CaptureName"), productOf = Export(Of ProductCall)("CaptureProduct")
            For i = 0 To CInt(count) - 1
                Dim index = CUInt(i)
                Dim id As New StringBuilder(128), title As New StringBuilder(512), kind As New StringBuilder(128)
                If describe(handle, index, id, 128, title, 512) <> 0 AndAlso productOf(handle, index, kind, 128) <> 0 Then Devices.Add(New Device(index, id.ToString(), title.ToString(), kind.ToString()))
            Next
        Catch
            Dispose()
            Throw
        End Try
    End Sub

    Public Function Read(device As Device) As Sample
        ObjectDisposedException.ThrowIf(handle = IntPtr.Zero, Me)
        Dim value = Sample.Empty()
        readNative(handle, device.Index, value)
        Return value
    End Function

    Public ReadOnly Property FeedbackAvailable As Boolean
        Get
            Return feedbackNative IsNot Nothing
        End Get
    End Property

    ' Plays (milliseconds > 0) or stops (0) the reader's short test effect.
    Public Function PlayFeedback(device As Device, milliseconds As UInteger) As Integer
        ObjectDisposedException.ThrowIf(handle = IntPtr.Zero, Me)
        Return If(feedbackNative Is Nothing, 0, feedbackNative(handle, device.Index, milliseconds))
    End Function

    ' The Windows call that refused the last test (index into FeedbackSteps, 0 if none) and its HRESULT.
    Public Function FeedbackFailure(ByRef result As Integer) As UInteger
        result = 0
        If handle = IntPtr.Zero OrElse feedbackErrorNative Is Nothing Then Return 0
        Return feedbackErrorNative(handle, result)
    End Function

    ' CreateEffect results of the last test's shapes, in FeedbackShapes order.
    Public Function FeedbackShapeResults() As Integer()
        If handle = IntPtr.Zero OrElse feedbackShapesNative Is Nothing Then Return Array.Empty(Of Integer)()
        Dim results(FeedbackShapes.Length - 1) As Integer
        Dim count = CInt(Math.Min(feedbackShapesNative(handle, results, CUInt(results.Length)), CUInt(results.Length)))
        Return results.Take(count).ToArray()
    End Function

    Public Shared Function FeedbackMessage(device As Device, result As Integer, failedStep As UInteger, failedResult As Integer, shapes As Integer()) As String
        Dim message As String
        Select Case result
            Case 1 : message = If(device.Xbox, "Rumbling " & device.Name & " for one second.", "Playing a force-feedback shake on " & device.Name & " for one second.")
            Case -1 : message = device.Name & " reports no force feedback."
            Case -2 : message = device.Name & " is in use by another program. Close DiRT 3 (or other games) and try again."
            Case -3 : message = "Windows could not start force feedback on " & device.Name & "."
            Case Else : message = "The feedback test is unavailable for " & device.Name & "."
        End Select
        ' Name the refusing Windows call, or every refused effect shape, so a report identifies it.
        If shapes Is Nothing Then shapes = Array.Empty(Of Integer)()
        If result < 0 AndAlso failedStep > 0UI AndAlso failedStep < CUInt(FeedbackSteps.Length) Then
            If failedStep = 6UI AndAlso shapes.Length > 1 Then
                message &= " (CreateEffect: " & String.Join(", ", shapes.Select(Function(code, i) FeedbackShapes(i) & " 0x" & code.ToString("X8", Globalization.CultureInfo.InvariantCulture))) & ")"
            Else
                message &= " (" & FeedbackSteps(CInt(failedStep)) & " returned 0x" & failedResult.ToString("X8", Globalization.CultureInfo.InvariantCulture) & ")"
            End If
        ElseIf result = 1 AndAlso shapes.Length > 1 Then
            message &= " (using " & FeedbackShapes(Math.Min(shapes.Length, FeedbackShapes.Length) - 1) & ")"
        End If
        Return message
    End Function

    Public Shared Function PovNeutral(value As UInteger) As Boolean
        Return (value And &HFFFFUI) = &HFFFFUI
    End Function

    Public Shared Function ButtonsText(device As Device, value As Sample) As String
        If value.Connected = 0UI Then Return "Disconnected"
        Dim pressed As New List(Of String)
        For i = 0 To 127
            If value.Buttons(i) = 0 Then Continue For
            If device.Xbox Then
                If i < XboxButtonNames.Length AndAlso XboxButtonNames(i).Length <> 0 Then pressed.Add(XboxButtonNames(i))
            Else
                pressed.Add(i.ToString(Globalization.CultureInfo.InvariantCulture))
            End If
        Next
        Return If(pressed.Count = 0, "None", String.Join(", ", pressed))
    End Function

    Public Shared Function HatsText(value As Sample) As String
        If value.Connected = 0UI Then Return "Disconnected"
        Return String.Join("   ", Enumerable.Range(0, 4).Select(Function(i) i.ToString(Globalization.CultureInfo.InvariantCulture) & ": " &
            If(PovNeutral(value.Pov(i)), "center", (value.Pov(i) / 100D).ToString("0.##", Globalization.CultureInfo.InvariantCulture) & "°")))
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If handle <> IntPtr.Zero AndAlso closeNative IsNot Nothing Then closeNative(handle)
        handle = IntPtr.Zero
        If library <> IntPtr.Zero Then NativeLibrary.Free(library)
        library = IntPtr.Zero
    End Sub
End Class
