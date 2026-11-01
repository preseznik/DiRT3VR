Imports System.Drawing
Imports System.Windows.Forms

' Status and notice lines that can be selected and copied (drag, Ctrl+A, Ctrl+C or right-click), so
' players can paste them into reports. Adapted from GRID2VR's SelectableStatusText: a read-only edit
' control that looks like a label and wraps up to six lines before it scrolls.
Public NotInheritable Class SelectableStatusText
    Inherits TextBox

    Private Const MaximumLines As Integer = 6
    Private fitting As Boolean

    Public Sub New()
        Me.ReadOnly = True : Multiline = True : WordWrap = True
        BorderStyle = BorderStyle.None : AutoSize = False
        ShortcutsEnabled = True : HideSelection = False : TabStop = True
        MaxLength = Integer.MaxValue
        Anchor = AnchorStyles.Left Or AnchorStyles.Right
        AccessibleDescription = "Read-only status. Select text or press Ctrl+A, then Ctrl+C to copy."
    End Sub

    ' Refreshing an unchanged message must not clear what the user selected.
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)>
    Public Overrides Property Text As String
        Get
            Return MyBase.Text
        End Get
        Set(value As String)
            Dim nextText = If(value, "")
            If Not String.Equals(MyBase.Text, nextText, StringComparison.Ordinal) Then MyBase.Text = nextText
        End Set
    End Property

    Public Sub FitWidth(width As Integer)
        width = Math.Max(1, width)
        MaximumSize = New Size(width, 0)
        Me.Width = width
        FitHeight()
    End Sub

    Protected Overrides Sub OnTextChanged(e As EventArgs)
        MyBase.OnTextChanged(e) : FitHeight()
    End Sub

    Protected Overrides Sub OnFontChanged(e As EventArgs)
        MyBase.OnFontChanged(e) : FitHeight()
    End Sub

    Protected Overrides Sub OnDpiChangedAfterParent(e As EventArgs)
        MyBase.OnDpiChangedAfterParent(e) : FitHeight()
    End Sub

    Private Function RequiredHeight(width As Integer, lineHeight As Integer, padding As Integer) As Integer
        width = Math.Max(1, width)
        Dim measured = TextRenderer.MeasureText(Text, Font, New Size(width, Integer.MaxValue),
            TextFormatFlags.WordBreak Or TextFormatFlags.TextBoxControl Or TextFormatFlags.NoPadding Or TextFormatFlags.NoPrefix)
        ' DT_EDITCONTROL may report one wide unbroken segment, such as a long path.
        Dim wrapped = CInt(Math.Ceiling(measured.Width / CDbl(width))) * lineHeight
        Return Math.Max(Math.Max(lineHeight, wrapped), measured.Height) + padding
    End Function

    Private Sub FitHeight()
        If fitting OrElse IsDisposed OrElse Not Multiline Then Return
        fitting = True
        Try
            If TextLength = 0 Then
                Height = 0
                Return
            End If
            Dim lineHeight = Math.Max(1, Font.Height)
            Dim padding = Math.Max(2, Px(Me, 2))
            Dim maximum = MaximumLines * lineHeight + padding
            Dim needed = RequiredHeight(Width - padding, lineHeight, padding)
            Dim bars = If(needed > maximum, ScrollBars.Vertical, ScrollBars.None)
            If ScrollBars <> bars Then
                Dim start = SelectionStart, length = SelectionLength
                ScrollBars = bars
                [Select](Math.Min(start, TextLength), Math.Min(length, Math.Max(0, TextLength - start)))
            End If
            If bars = ScrollBars.Vertical Then needed = RequiredHeight(Width - padding - SystemInformation.VerticalScrollBarWidth, lineHeight, padding)
            Height = Math.Min(maximum, needed)
        Finally
            fitting = False
        End Try
    End Sub
End Class

' Disabled WinForms buttons get no mouse events, so their tooltips never show. This watches the
' registered launch buttons and shows why one is disabled while the pointer rests on it (adapted from
' DiRT2VR's LaunchButtonTips). The reason is also the button's accessible description.
Public NotInheritable Class LaunchButtonTips
    Implements IDisposable

    Private ReadOnly owner As Form
    Private ReadOnly tips As ToolTip
    Private ReadOnly reasons As New Dictionary(Of Control, String)
    Private ReadOnly pollTimer As New Timer With {.Interval = 100}
    Private hovered As Control
    Private hoverStarted As Long
    Private _ShownReason As String

    Public Sub New(owner As Form, tips As ToolTip)
        Me.owner = owner : Me.tips = tips
        AddHandler pollTimer.Tick, Sub() Poll(Cursor.Position, owner.Visible AndAlso Form.ActiveForm Is owner, Environment.TickCount64)
        pollTimer.Start()
    End Sub

    ' The hover text for a button: its usual tooltip, or why it is disabled right now.
    Public Sub Update(button As Control, usual As String, reason As String)
        reason = If(button.Enabled, "", If(reason, ""))
        reasons(button) = reason
        Dim description = If(reason = "", usual, reason)
        If button.AccessibleDescription <> description Then button.AccessibleDescription = description
        If tips.GetToolTip(button) <> description Then tips.SetToolTip(button, description)
        If hovered Is button AndAlso _ShownReason IsNot Nothing AndAlso _ShownReason <> reason Then Reset()
    End Sub

    Public Function Reason(button As Control) As String
        Dim value As String = Nothing
        Return If(reasons.TryGetValue(button, value), value, "")
    End Function

    ' The reason currently shown in the hover tip, if any (for the launcher tests).
    Public ReadOnly Property ShownReason As String
        Get
            Return _ShownReason
        End Get
    End Property

    Public Sub Poll(pointer As Point, active As Boolean, nowMs As Long)
        Dim target As Control = Nothing
        If active Then
            target = reasons.Keys.FirstOrDefault(Function(button) Not button.Enabled AndAlso button.Visible AndAlso reasons(button) <> "" AndAlso
                                                     button.RectangleToScreen(button.ClientRectangle).Contains(pointer))
        End If
        If target IsNot hovered Then
            Reset() : hovered = target : hoverStarted = nowMs
        End If
        If target Is Nothing OrElse _ShownReason IsNot Nothing OrElse nowMs - hoverStarted < tips.InitialDelay Then Return
        Dim location = owner.PointToClient(pointer)
        _ShownReason = reasons(target)
        tips.Show(_ShownReason, owner, New Point(location.X, location.Y + Px(owner, 22)), tips.AutoPopDelay)
    End Sub

    Private Sub Reset()
        If _ShownReason IsNot Nothing AndAlso Not owner.IsDisposed Then tips.Hide(owner)
        hovered = Nothing : _ShownReason = Nothing
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        pollTimer.Stop() : pollTimer.Dispose()
    End Sub
End Class
