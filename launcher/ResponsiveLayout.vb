Imports System.Drawing
Imports System.Windows.Forms

Public Module LauncherLayout
    Public Function Px(control As Control, value As Integer) As Integer
        Return CInt(value * control.DeviceDpi / 96.0)
    End Function
    Public Function Stack() As VerticalStack
        Return New VerticalStack With {.Dock = DockStyle.Top, .Margin = New Padding(0)}
    End Function
    ' Create every launcher section through here: its heading collapses the section,
    ' and MainForm remembers the state. Launcher tests reject plain heading labels.
    Public Function Section(parent As Control, title As String) As VerticalStack
        Dim panel = Stack()
        panel.Margin = New Padding(0, 0, 0, 14)
        panel.Controls.Add(New SectionHeader(title) With {.Font = New Font("Segoe UI", 11, FontStyle.Bold), .Margin = New Padding(0, 8, 0, 10)})
        parent.Controls.Add(panel)
        Return panel
    End Function
    Public Sub Field(parent As Control, title As String, value As Control)
        parent.Controls.Add(New SettingRow(title, value))
    End Sub
    Public Sub StyleChoice(box As ComboBox, owner As Control)
        box.FlatStyle = FlatStyle.Flat
        box.DrawMode = DrawMode.OwnerDrawFixed
        Dim updateColors As Action = Sub()
                                         box.BackColor = owner.BackColor : box.ForeColor = owner.ForeColor
                                     End Sub
        AddHandler owner.BackColorChanged, Sub() updateColors()
        AddHandler owner.ForeColorChanged, Sub() updateColors()
        updateColors()
        AddHandler box.DrawItem, AddressOf DrawChoice
        AddHandler box.DropDown, Sub() box.DropDownWidth = Math.Min(Px(box, 600), Screen.FromControl(box).WorkingArea.Width)
    End Sub
    Private Sub DrawChoice(sender As Object, e As DrawItemEventArgs)
        Dim box = DirectCast(sender, ComboBox)
        Dim highlighted = (e.State And DrawItemState.Selected) <> 0 AndAlso (e.State And DrawItemState.ComboBoxEdit) = 0
        Dim background = If(highlighted, SystemColors.Highlight, box.BackColor)
        Dim foreground = If(Not box.Enabled, SystemColors.GrayText, If(highlighted, SystemColors.HighlightText, box.ForeColor))
        Using brush As New SolidBrush(background)
            e.Graphics.FillRectangle(brush, e.Bounds)
        End Using
        If e.Index >= 0 Then
            TextRenderer.DrawText(e.Graphics, box.GetItemText(box.Items(e.Index)), box.Font, e.Bounds, foreground, TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis Or TextFormatFlags.NoPrefix)
        End If
        e.DrawFocusRectangle()
    End Sub
    Public Function HelpLink(action As Action) As LinkLabel
        Dim link As New LinkLabel With {.Text = "Instructions", .AutoSize = True, .Margin = New Padding(0, 10, 0, 10), .Name = "InstructionsLink"}
        Dim updateColor As Action = Sub()
                                        link.LinkColor = If(link.BackColor.GetBrightness() < 0.5F, Color.LightSkyBlue, Color.FromArgb(0, 80, 160))
                                        link.ActiveLinkColor = link.LinkColor
                                        link.VisitedLinkColor = link.LinkColor
                                    End Sub
        AddHandler link.BackColorChanged, Sub() updateColor()
        AddHandler link.ForeColorChanged, Sub() updateColor()
        updateColor()
        AddHandler link.LinkClicked, Sub() action()
        Return link
    End Function
End Module

' A focusable section heading (adapted from GRID2VR). Collapsing hides the rows
' after it in its stack; rows are never removed, recreated or reordered.
Public Class SectionHeader
    Inherits Button
    Private collapsedValue As Boolean, hot As Boolean
    Public ReadOnly Property Title As String
    Public Event CollapsedChanged As EventHandler
    Public Sub New(title As String)
        Me.Title = title
        Name = "Section" & New String(title.Where(AddressOf Char.IsLetterOrDigit).ToArray())
        Tag = "section" : AutoSize = True : TextAlign = ContentAlignment.MiddleLeft : UseMnemonic = False : Cursor = Cursors.Hand
        Padding = New Padding(0) : FlatStyle = FlatStyle.Flat : FlatAppearance.BorderSize = 0 : AccessibleName = title
        UpdateText()
    End Sub
    ' Painted directly: dark-mode button rendering draws a border even at BorderSize 0.
    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim back = If(hot AndAlso Not FlatAppearance.MouseOverBackColor.IsEmpty, FlatAppearance.MouseOverBackColor, BackColor)
        Using brush As New SolidBrush(back)
            e.Graphics.FillRectangle(brush, ClientRectangle)
        End Using
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, If(Enabled, ForeColor, SystemColors.GrayText),
                              TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix)
        If Focused AndAlso ShowFocusCues Then ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, ForeColor, back)
    End Sub
    Protected Overrides Sub OnMouseEnter(e As EventArgs)
        hot = True : Invalidate()
        MyBase.OnMouseEnter(e)
    End Sub
    Protected Overrides Sub OnMouseLeave(e As EventArgs)
        hot = False : Invalidate()
        MyBase.OnMouseLeave(e)
    End Sub
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property Collapsed As Boolean
        Get
            Return collapsedValue
        End Get
        Set(value As Boolean)
            If collapsedValue = value Then Return
            collapsedValue = value : UpdateText() : Parent?.PerformLayout()
            RaiseEvent CollapsedChanged(Me, EventArgs.Empty)
        End Set
    End Property
    Protected Overrides Sub OnClick(e As EventArgs)
        Collapsed = Not Collapsed
        MyBase.OnClick(e)
    End Sub
    Private Sub UpdateText()
        Text = If(collapsedValue, "▸ ", "▾ ") & Title
        AccessibleDescription = If(collapsedValue, "Collapsed section; press to expand", "Expanded section; press to collapse")
    End Sub
End Class

' Explicit vertical flow avoids TableLayoutPanel retaining tall row allocations after reflow.
Public Class VerticalStack
    Inherits Panel
    Private arranging As Boolean
    ' Rows hidden by a collapsed heading. Launcher rows are otherwise always shown,
    ' so expanding restores exactly these.
    Private ReadOnly collapsedHidden As New HashSet(Of Control)
    Protected Overrides Sub OnControlAdded(e As ControlEventArgs)
        e.Control.Dock = DockStyle.None
        AddHandler e.Control.SizeChanged, AddressOf ChildSizeChanged
        MyBase.OnControlAdded(e)
    End Sub
    Protected Overrides Sub OnControlRemoved(e As ControlEventArgs)
        RemoveHandler e.Control.SizeChanged, AddressOf ChildSizeChanged
        If collapsedHidden.Remove(e.Control) Then e.Control.Visible = True
        MyBase.OnControlRemoved(e)
    End Sub
    Private Sub ChildSizeChanged(sender As Object, e As EventArgs)
        PerformLayout()
    End Sub
    Protected Overrides Sub OnLayout(e As LayoutEventArgs)
        MyBase.OnLayout(e)
        If arranging Then Return
        arranging = True
        Try
            Dim y = Padding.Top, collapsed = False
            For Each child As Control In Controls
                Dim header = TryCast(child, SectionHeader)
                If header IsNot Nothing Then collapsed = header.Collapsed
                If collapsed AndAlso header Is Nothing Then
                    ' Hidden rows also leave the tab order.
                    collapsedHidden.Add(child) : child.Visible = False
                    Continue For
                End If
                If collapsedHidden.Remove(child) Then child.Visible = True
                Dim width = Math.Max(1, ClientSize.Width - Padding.Horizontal - child.Margin.Horizontal)
                Dim fixedWidth = TypeOf child Is Button OrElse TypeOf child Is LinkLabel
                child.SetBounds(Padding.Left + child.Margin.Left, y + child.Margin.Top, If(fixedWidth, Math.Min(width, child.PreferredSize.Width), width), child.Height)
                If (TypeOf child Is Label AndAlso child.AutoSize) OrElse TypeOf child Is Button OrElse TypeOf child Is FlowLayoutPanel Then
                    child.Height = child.GetPreferredSize(New Size(width, 0)).Height
                End If
                y = child.Bottom + child.Margin.Bottom
            Next
            Height = y + Padding.Bottom
        Finally
            arranging = False
        End Try
    End Sub
    Public Overrides Function GetPreferredSize(proposedSize As Size) As Size
        ' Scroll layout probes with unconstrained widths (65535). Do not turn that
        ' measurement into a transient giant window for every native child.
        Return New Size(Width, Height)
    End Function
End Class

' Moving bounds preserves control instances, keyboard focus and in-progress input capture.
Public Class SettingRow
    Inherits Panel
    Private ReadOnly caption As Label
    Private ReadOnly editor As Control
    Private arranging As Boolean
    Public Sub New(title As String, value As Control)
        Dock = DockStyle.Top : Margin = New Padding(0) : TabStop = False : AutoSize = True : AutoSizeMode = AutoSizeMode.GrowAndShrink
        caption = New Label With {.Text = title, .AutoSize = False, .TextAlign = ContentAlignment.MiddleLeft, .Name = value.Name & "Label"}
        editor = value : editor.Dock = DockStyle.None : editor.Margin = New Padding(0)
        If editor.AccessibleName Is Nothing Then editor.AccessibleName = title
        Controls.Add(caption) : Controls.Add(editor)
    End Sub
    ' The row's label; Simple view renames a few rows while it borrows them.
    <System.ComponentModel.Browsable(False), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property CaptionText As String
        Get
            Return caption.Text
        End Get
        Set(value As String)
            caption.Text = value : PerformLayout()
        End Set
    End Property
    Protected Overrides Sub OnLayout(e As LayoutEventArgs)
        MyBase.OnLayout(e)
        If arranging OrElse editor Is Nothing OrElse ClientSize.Width <= 0 Then Return
        arranging = True
        Try
            Dim compact = TypeOf editor Is ComboBox
            Dim gap = Px(Me, If(compact, 8, 10)), padding = Px(Me, If(compact, 3, 5)), width = ClientSize.Width
            Dim stacked = width < Px(Me, If(compact, 380, 590)) AndAlso Not TypeOf editor Is CheckBox
            Dim labelWidth = If(compact, Math.Min(Px(Me, 170), CInt(width * 0.4)), Math.Min(Px(Me, 215), width \ 2))
            Dim editorWidth = If(stacked, width, width - labelWidth - gap)
            Dim editorHeight = If(TypeOf editor Is ValueSlider, Px(Me, DirectCast(editor, ValueSlider).LogicalHeight), editor.GetPreferredSize(New Size(editorWidth, 0)).Height)
            editorHeight = Math.Max(Px(Me, 28), editorHeight)
            Dim labelHeight = TextRenderer.MeasureText(caption.Text, Font, New Size(If(stacked, width, labelWidth), 0), TextFormatFlags.WordBreak Or TextFormatFlags.NoPrefix).Height
            Dim rowHeight = If(stacked, labelHeight + editorHeight + gap, Math.Max(labelHeight, editorHeight))
            caption.SetBounds(0, padding, If(stacked, width, labelWidth), If(stacked, labelHeight, rowHeight))
            editor.SetBounds(If(stacked, 0, labelWidth + gap), padding + If(stacked, labelHeight + gap, (rowHeight - editorHeight) \ 2), editorWidth, editorHeight)
            Height = rowHeight + padding * 2
        Finally
            arranging = False
        End Try
    End Sub
    Public Overrides Function GetPreferredSize(proposedSize As Size) As Size
        Return New Size(Width, Height)
    End Function
End Class

Public Class ResponsiveColumns
    Inherits Panel
    <System.ComponentModel.DefaultValue(1060)>
    Public Property WideAt As Integer = 1060
    Public ReadOnly First As VerticalStack = Stack()
    Public ReadOnly Second As VerticalStack = Stack()
    Private arranging As Boolean
    Public Sub New()
        Dock = DockStyle.Top : Margin = New Padding(0) : TabStop = False : AutoSize = True : AutoSizeMode = AutoSizeMode.GrowAndShrink
        First.Dock = DockStyle.None : Second.Dock = DockStyle.None
        Controls.Add(First) : Controls.Add(Second)
        AddHandler First.SizeChanged, Sub() PerformLayout()
        AddHandler Second.SizeChanged, Sub() PerformLayout()
    End Sub
    Protected Overrides Sub OnLayout(e As LayoutEventArgs)
        MyBase.OnLayout(e)
        If arranging OrElse First Is Nothing OrElse Width <= 0 Then Return
        arranging = True
        Try
            Dim wide = Width >= Px(Me, WideAt), gap = Px(Me, 30)
            Dim firstWidth = If(wide, CInt((Width - gap) * 0.54), Width)
            First.SetBounds(0, 0, firstWidth, First.GetPreferredSize(New Size(firstWidth, 0)).Height)
            Dim secondWidth = If(wide, Width - firstWidth - gap, Width)
            Second.SetBounds(If(wide, firstWidth + gap, 0), If(wide, 0, First.Height), secondWidth, Second.GetPreferredSize(New Size(secondWidth, 0)).Height)
            Height = If(wide, Math.Max(First.Height, Second.Height), First.Height + Second.Height)
        Finally
            arranging = False
        End Try
    End Sub
    Public Overrides Function GetPreferredSize(proposedSize As Size) As Size
        Return New Size(Width, Height)
    End Function
End Class

Public Class LauncherFooter
    Inherits Panel
    Private ReadOnly launch As Button, vr As Button, save As Button, recover As Button, logs As Button, stopVr As Button
    Private arranging As Boolean
    ' stopHeadset, when given, sits right of the VR button and only takes space while visible.
    Public Sub New(desktop As Button, headset As Button, settings As Button, restore As Button, openLogs As Button, Optional stopHeadset As Button = Nothing)
        launch = desktop : vr = headset : save = settings : recover = restore : logs = openLogs : stopVr = stopHeadset
        Dock = DockStyle.Fill : Margin = New Padding(0, 12, 0, 0)
        Controls.AddRange({launch, vr, save, recover, logs})
        If stopVr IsNot Nothing Then Controls.Add(stopVr)
        For Each button In Controls.OfType(Of Button)()
            AddHandler button.VisibleChanged, Sub() PerformLayout()
        Next
    End Sub
    Protected Overrides Sub OnLayout(e As LayoutEventArgs)
        MyBase.OnLayout(e)
        If arranging OrElse launch Is Nothing Then Return
        arranging = True
        Try
            Dim gap = Px(Me, 8)
            For Each button In Controls.OfType(Of Button)()
                button.Size = button.GetPreferredSize(Size.Empty)
            Next
            Dim rowHeight = Controls.OfType(Of Button)().Max(Function(b) b.Height)
            Dim showStop = stopVr IsNot Nothing AndAlso stopVr.Visible
            Dim leftWidth = If(launch.Visible, launch.Width + vr.Width + gap, 0) + If(showStop, stopVr.Width + gap, 0)
            Dim utilityWidth = save.Width + recover.Width + logs.Width + gap * 2
            Dim compact = Width < leftWidth + utilityWidth + gap * 2
            launch.Location = New Point(0, 0) : vr.Location = New Point(launch.Right + gap, 0)
            If showStop Then stopVr.Location = New Point(vr.Right + gap, 0)
            logs.Location = New Point(Width - logs.Width, If(compact, rowHeight + gap, 0))
            recover.Location = New Point(logs.Left - gap - recover.Width, logs.Top)
            ' In two rows, Save settings joins the second row when the session buttons fill the first.
            Dim crowded = compact AndAlso leftWidth + save.Width + gap > Width
            save.Location = If(crowded, New Point(recover.Left - gap - save.Width, logs.Top), New Point(If(compact, Width - save.Width, recover.Left - gap - save.Width), 0))
            Height = rowHeight * If(compact, 2, 1) + If(compact, gap, 0) + Px(Me, 4)
        Finally
            arranging = False
        End Try
    End Sub
    Public Overrides Function GetPreferredSize(proposedSize As Size) As Size
        Return New Size(Width, Height)
    End Function
End Class
