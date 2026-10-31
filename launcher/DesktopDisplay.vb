Imports System.Drawing
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

Public Module DesktopDisplay
    ' DEVMODEW display fields. Explicit offsets avoid the printer/display union.
    <StructLayout(LayoutKind.Explicit, Size:=220)>
    Private Structure DisplayMode
        <FieldOffset(68)> Public Size As UShort
        <FieldOffset(168)> Public BitsPerPixel As UInteger
        <FieldOffset(172)> Public Width As UInteger
        <FieldOffset(176)> Public Height As UInteger
    End Structure
    <DllImport("user32.dll", CharSet:=CharSet.Unicode)>
    Private Function EnumDisplaySettings(device As String, mode As Integer, ByRef value As DisplayMode) As Boolean
    End Function

    Public Function MainBounds() As Rectangle
        Dim display = Screen.PrimaryScreen
        If display Is Nothing Then Throw New IOException("The main display is unavailable.")
        Return display.Bounds
    End Function

    Public Function Resolutions() As Size()
        Dim display = Screen.PrimaryScreen
        If display Is Nothing Then Return Array.Empty(Of Size)()
        Dim sizes As New HashSet(Of Size)
        For index = 0 To 4095
            Dim mode As New DisplayMode With {.Size = 220}
            If Not EnumDisplaySettings(display.DeviceName, index, mode) Then Exit For
            If mode.BitsPerPixel = 32 AndAlso mode.Width >= 640 AndAlso mode.Height >= 480 AndAlso mode.Width <= 8192 AndAlso mode.Height <= 8192 Then sizes.Add(New Size(CInt(mode.Width), CInt(mode.Height)))
        Next
        Return sizes.OrderBy(Function(s) s.Width).ThenBy(Function(s) s.Height).ToArray()
    End Function
End Module

Public Class DesktopResolutionChoice
    Public ReadOnly Size As Size
    Public Sub New(value As Size)
        Size = value
    End Sub
    Public Overrides Function ToString() As String
        Return If(Size = Size.Empty, "Use game setting", $"{Size.Width} × {Size.Height}")
    End Function
End Class
