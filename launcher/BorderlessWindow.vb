Imports System.ComponentModel
Imports System.Drawing
Imports System.Runtime.InteropServices

' Adapted from DiRT2VR. Called only for the launched desktop process's NeonClass_41 window.
' Never activates the window, changes the monitor mode, or repeatedly fights user changes.
Public Class BorderlessWindow
    Public Const FrameBits As Long = &HC00000L Or &H40000L Or &H80000L Or &H30000L
    Private Const EdgeBits As Long = &H1L Or &H100L Or &H200L Or &H20000L
    Private Const PositionFlags As UInteger = &H10UI Or &H20UI Or &H200UI Or &H4000UI
    Private ReadOnly processId As Integer
    Private ReadOnly target As Rectangle
    Private window As IntPtr
    Private style, exStyle, deadline As Long
    Private original As Rectangle
    Private complete As Boolean
    Public Property Warning As String = ""

    Public Sub New(owner As Integer, bounds As Rectangle)
        DesktopGraphics.ValidateSize(bounds.Size)
        processId = owner : target = bounds
    End Sub

    ' The monitor checks process start time and render-window class before calling this.
    ' The lower level also permits owned WinForms windows for tests without starting a game.
    Public Function UpdateWindow(candidate As IntPtr, now As Long) As Boolean
        Dim owner As Integer
        If candidate = IntPtr.Zero OrElse GetWindowThreadProcessId(candidate, owner) = 0 OrElse owner <> processId Then Return False
        If complete Then Return True
        If window <> IntPtr.Zero AndAlso window <> candidate Then
            Warning = "The game window changed during startup; borderless was not applied to the replacement window."
            complete = True : Return True
        End If
        If IsIconic(candidate) OrElse IsHungAppWindow(candidate) Then Return False
        Try
            If window = IntPtr.Zero Then
                original = ReadBounds(candidate)
                style = ReadStyle(candidate, -16) : exStyle = ReadStyle(candidate, -20)
                window = candidate : deadline = now + 5000
                WriteStyle(window, -16, (style And Not FrameBits) Or &H80000000L)
                WriteStyle(window, -20, exStyle And Not EdgeBits)
                Position(window, target, If((exStyle And 8) <> 0, CType(-2, Integer?), Nothing))
            ElseIf ReadBounds(window) = target AndAlso (ReadStyle(window, -16) And FrameBits) = 0 AndAlso (ReadStyle(window, -20) And (EdgeBits Or 8)) = 0 Then
                complete = True
            ElseIf now >= deadline Then
                Throw New IOException("The game did not accept the borderless window size.")
            End If
        Catch ex As Exception When TypeOf ex Is Win32Exception OrElse TypeOf ex Is IOException
            complete = True
            Warning = "Borderless unavailable; using windowed mode. " & ex.Message
            RestoreWindow()
        End Try
        Return complete
    End Function

    Public Sub Cancel()
        If complete OrElse window = IntPtr.Zero Then Return
        complete = True
        Warning = "Borderless setup was interrupted; restoring windowed mode."
        RestoreWindow()
    End Sub

    Private Sub RestoreWindow()
        Dim owner As Integer
        If window = IntPtr.Zero OrElse GetWindowThreadProcessId(window, owner) = 0 OrElse owner <> processId Then Return
        Try
            WriteStyle(window, -16, style) : WriteStyle(window, -20, exStyle)
            Position(window, original, If((exStyle And 8) <> 0, -1, -2))
        Catch rollback As Win32Exception
            Warning &= " Window restoration failed: " & rollback.Message
        End Try
    End Sub

    Public Shared Function ReadStyle(window As IntPtr, index As Integer) As Long
        Marshal.SetLastPInvokeError(0)
        Dim value = GetWindowLongPtrW(window, index)
        If value = IntPtr.Zero AndAlso Marshal.GetLastPInvokeError() <> 0 Then Throw New Win32Exception(Marshal.GetLastPInvokeError())
        Return value.ToInt64()
    End Function
    Private Shared Sub WriteStyle(window As IntPtr, index As Integer, value As Long)
        Marshal.SetLastPInvokeError(0)
        If SetWindowLongPtrW(window, index, New IntPtr(value)) = IntPtr.Zero AndAlso Marshal.GetLastPInvokeError() <> 0 Then Throw New Win32Exception(Marshal.GetLastPInvokeError())
    End Sub
    Public Shared Function ReadBounds(window As IntPtr) As Rectangle
        Dim value As New WindowRect
        If Not GetWindowRect(window, value) Then Throw New Win32Exception(Marshal.GetLastPInvokeError())
        Return Rectangle.FromLTRB(value.Left, value.Top, value.Right, value.Bottom)
    End Function
    Private Shared Sub Position(window As IntPtr, target As Rectangle, order As Integer?)
        If Not SetWindowPos(window, New IntPtr(order.GetValueOrDefault()), target.X, target.Y, target.Width, target.Height,
                            PositionFlags Or If(order.HasValue, 0UI, &H4UI)) Then Throw New Win32Exception(Marshal.GetLastPInvokeError())
    End Sub
    <StructLayout(LayoutKind.Sequential)>
    Private Structure WindowRect
        Public Left, Top, Right, Bottom As Integer
    End Structure
    <DllImport("user32.dll", SetLastError:=True)> Private Shared Function GetWindowLongPtrW(window As IntPtr, index As Integer) As IntPtr
    End Function
    <DllImport("user32.dll", SetLastError:=True)> Private Shared Function SetWindowLongPtrW(window As IntPtr, index As Integer, value As IntPtr) As IntPtr
    End Function
    <DllImport("user32.dll", SetLastError:=True)> Private Shared Function SetWindowPos(window As IntPtr, after As IntPtr, x As Integer, y As Integer, width As Integer, height As Integer, flags As UInteger) As Boolean
    End Function
    <DllImport("user32.dll", SetLastError:=True)> Private Shared Function GetWindowRect(window As IntPtr, ByRef value As WindowRect) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function GetWindowThreadProcessId(window As IntPtr, ByRef owner As Integer) As UInteger
    End Function
    <DllImport("user32.dll")> Private Shared Function IsIconic(window As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function IsHungAppWindow(window As IntPtr) As Boolean
    End Function
End Class
