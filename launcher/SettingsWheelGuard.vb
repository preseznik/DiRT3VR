Imports System.Drawing
Imports System.Runtime.CompilerServices
Imports System.Windows.Forms

' Application-local message filtering, not an input hook (adapted from GRID2VR).
' Wheel messages addressed to this form's value editors scroll their containing
' page before native controls can change a value. Focus never arms wheel editing.
' No input/window messages are synthesized or forwarded, and focus is never moved.
Public NotInheritable Class SettingsWheelGuard
    Implements IMessageFilter, IDisposable
    Public Const MouseWheel As Integer = &H20A, MouseHWheel As Integer = &H20E
    Private ReadOnly owner As Form
    Private ReadOnly scrollLines As Func(Of Integer)
    Private ReadOnly dropdownOpen As Func(Of ComboBox, Boolean)
    Private ReadOnly remainders As New ConditionalWeakTable(Of ScrollableControl, WheelRemainder)()
    Private disposed As Boolean
    Private NotInheritable Class WheelRemainder
        Public Vertical, Horizontal As Integer
    End Class

    Public Sub New(owner As Form, Optional scrollLines As Func(Of Integer) = Nothing, Optional dropdownOpen As Func(Of ComboBox, Boolean) = Nothing)
        If scrollLines Is Nothing Then scrollLines = Function() SystemInformation.MouseWheelScrollLines
        If dropdownOpen Is Nothing Then dropdownOpen = Function(combo) combo.DroppedDown
        Me.owner = owner : Me.scrollLines = scrollLines : Me.dropdownOpen = dropdownOpen
        Application.AddMessageFilter(Me)
    End Sub

    Public ReadOnly Property IsDisposed As Boolean
        Get
            Return disposed
        End Get
    End Property

    Public Shared Function IsValueEditor(control As Control) As Boolean
        Return TypeOf control Is TrackBar OrElse TypeOf control Is ComboBox OrElse TypeOf control Is NumericUpDown
    End Function

    Public Function PreFilterMessage(ByRef m As Message) As Boolean Implements IMessageFilter.PreFilterMessage
        If disposed OrElse owner.IsDisposed OrElse owner.Disposing OrElse (m.Msg <> MouseWheel AndAlso m.Msg <> MouseHWheel) Then Return False
        Dim target = Control.FromChildHandle(m.HWnd)
        If target Is Nothing OrElse target.IsDisposed OrElse target.FindForm() IsNot owner Then Return False
        ' NumericUpDown and editable ComboBox have native child windows. Resolve
        ' the editor from its ancestry, not just the message's immediate handle.
        Dim editor = target
        While editor IsNot Nothing AndAlso editor IsNot owner AndAlso Not IsValueEditor(editor)
            editor = editor.Parent
        End While
        If editor Is Nothing OrElse editor Is owner Then Return False
        Dim combo = TryCast(editor, ComboBox)
        If combo IsNot Nothing AndAlso dropdownOpen(combo) Then Return False
        Dim word = CInt((m.WParam.ToInt64() >> 16) And &HFFFFL)
        ScrollPage(editor, If(word >= &H8000, word - &H10000, word), m.Msg = MouseHWheel)
        m.Result = IntPtr.Zero
        Return True ' Even a dialog/page without scrolling must not edit a value.
    End Function

    Private Sub ScrollPage(editor As Control, delta As Integer, horizontal As Boolean)
        If delta = 0 Then Return
        Dim direction = If(horizontal, delta, -delta)
        Dim parent = editor.Parent
        While parent IsNot Nothing
            Dim page = TryCast(parent, ScrollableControl)
            parent = parent.Parent
            If page Is Nothing OrElse Not page.AutoScroll OrElse page.IsDisposed Then Continue While
            Dim scroll As ScrollProperties = If(horizontal, DirectCast(page.HorizontalScroll, ScrollProperties), page.VerticalScroll)
            If Not scroll.Visible OrElse Not scroll.Enabled Then Continue While
            Dim minimum = scroll.Minimum
            Dim maximum = Math.Max(minimum, scroll.Maximum - scroll.LargeChange + 1)
            Dim current = scroll.Value
            If maximum <= minimum OrElse If(direction > 0, current >= maximum, current <= minimum) Then Continue While
            Dim remainder = remainders.GetOrCreateValue(page)
            Dim lines = scrollLines()
            Dim total = If(horizontal, remainder.Horizontal, remainder.Vertical) + direction
            Dim steps = total \ SystemInformation.MouseWheelScrollDelta
            ' Keep high-resolution wheel/trackpad remainders on the page until they make a
            ' whole notch. Zero lines means Windows has disabled wheel scrolling.
            Dim pending = If(lines = 0, 0, total Mod SystemInformation.MouseWheelScrollDelta)
            If horizontal Then remainder.Horizontal = pending Else remainder.Vertical = pending
            If lines = 0 OrElse steps = 0 Then Return
            Dim unit = If(lines < 0, CLng(scroll.LargeChange), Math.Min(Integer.MaxValue, CLng(scroll.SmallChange) * lines))
            Dim nextValue = CInt(Math.Clamp(current + steps * unit, CLng(minimum), CLng(maximum)))
            Dim position = page.AutoScrollPosition
            page.AutoScrollPosition = If(horizontal, New Point(nextValue, -position.Y), New Point(-position.X, nextValue))
            Return
        End While
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If disposed Then Return
        disposed = True
        Application.RemoveMessageFilter(Me)
        remainders.Clear()
    End Sub
End Class
