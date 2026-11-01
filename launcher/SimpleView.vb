Imports System.Drawing
Imports System.Windows.Forms

' Simple and Advanced views (adapted from GRID2VR's MainForm.Views, LauncherViewPicker and
' AdvancedViewConfirmationForm). Simple replaces the tabs with one Quick start page that borrows the
' essential controls: each is moved there and back (its original parent and position are recorded),
' never duplicated, so a choice made in one view shows in the other and switching views never changes
' or saves a setting. Advanced, the default, shows every tab.
Partial Public Class MainForm
    Public Const HelpViews As String = "Simple and Advanced views"
    Private ReadOnly viewPicker As New LauncherViewPicker()
    Private ReadOnly viewPlacements As New List(Of ViewPlacement)
    Private fullPages As TabPage()
    Private quickPage As TabPage
    Private lastFullPage As TabPage
    Private restoringView As Boolean
    Private confirmAdvanced As Func(Of Boolean)

    Private NotInheritable Class ViewPlacement
        Public Moved As Control
        Public FullParent As Control
        Public FullIndex As Integer
        Public SimpleParent As Control
        Public FullCaption As String
        Public SimpleCaption As String
    End Class

    ' Asked before Simple switches to Advanced; the launcher tests replace the dialog.
    <System.ComponentModel.Browsable(False), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property ConfirmAdvancedView As Func(Of Boolean)
        Get
            Return If(confirmAdvanced, New Func(Of Boolean)(AddressOf AskAdvancedView))
        End Get
        Set(value As Func(Of Boolean))
            confirmAdvanced = value
        End Set
    End Property

    Public ReadOnly Property SimpleViewShown As Boolean
        Get
            Return quickPage IsNot Nothing AndAlso tabs.TabPages.Contains(quickPage)
        End Get
    End Property

    ' Builds Quick start and records where its controls come from; nothing moves until Simple is chosen.
    Private Sub InitializeViews()
        fullPages = tabs.TabPages.Cast(Of TabPage)().ToArray()
        lastFullPage = fullPages(0)
        quickPage = New TabPage("Quick start") With {.Name = "QuickStart", .Padding = New Padding(16), .AutoScroll = True, .UseVisualStyleBackColor = False}
        Dim content = Stack()
        content.Name = "QuickStartContent"
        quickPage.Controls.Add(content)
        AddHandler quickPage.SizeChanged, Sub()
                                              content.Width = Math.Max(1, quickPage.ClientSize.Width - quickPage.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth)
                                              RefreshLabelWidths(content)
                                          End Sub
        Dim columns As New ResponsiveColumns()
        content.Controls.Add(columns)
        ' Play: the Launch mode (with direct practice) and Startup sections, as they are.
        Borrow(SectionPanel("Launch mode"), columns.First)
        Borrow(SectionPanel("Startup"), columns.First)
        Dim headset = Section(columns.Second, "VR headset")
        Borrow(experimental, headset)
        Borrow(runtime.Parent, headset)
        headset.Controls.Add(Info("Connect the headset through Virtual Desktop and choose VDXR. Runtime locations, the runtime check and OpenXR add-ons are under Advanced → Settings."))
        Dim performance = Section(columns.Second, "Performance")
        Borrow(PresetRow("QualityPreset"), performance, "Desktop quality")
        Borrow(PresetRow("VrQualityPreset"), performance, "VR quality")
        Borrow(renderScale.Parent, performance)
        Borrow(potatoPreset.Parent, performance)
        Borrow(resolutionSummary, performance)
        Borrow(refreshHeadset, performance)
        Borrow(lastSessionSummary, performance)
        performance.Controls.Add(Info("The quality presets are DiRT 3's own. Potato / Low-End and VR Balanced change only VR. Display mode, resolution and each quality option are under Advanced → Graphics."))
        Dim comfort = Section(columns.Second, "Controls & comfort")
        Borrow(recenterButton.Parent, comfort)
        Borrow(recenterDeviceButton.Parent.Parent, comfort)
        Borrow(recenterKeepLevel, comfort)
        Dim profiles As New Button With {.Name = "SimpleProfiles", .Text = "Settings profiles…", .AutoSize = True}
        comfort.Controls.Add(profiles)
        AddHandler profiles.Click, Sub() ShowAdvancedTab("Settings")
        content.Controls.Add(Info("Advanced shows every option on separate tabs: graphics details, live input, VR runtime, profiles, defaults and diagnostics. Switching views keeps every choice and saves nothing."))
        content.Controls.Add(TopicLink(HelpViews))
        headers.AddRange(FindHeaders(quickPage))
        AddHandler viewPicker.ViewChanged, AddressOf ViewPickerChanged
        tips.SetToolTip(viewPicker, "Simple shows the essential choices on one page; Advanced shows every option. Switching never changes or saves settings.")
        AddHandler Disposed, Sub()
                                 For Each detached In fullPages.Append(quickPage)
                                     If detached.Parent Is Nothing Then detached.Dispose()
                                 Next
                             End Sub
        If WindowPreferences.Load(context).SimpleView Then viewPicker.Advanced = False
    End Sub

    Private Function SectionPanel(title As String) As Control
        Return headers.Single(Function(header) header.Title = title).Parent
    End Function

    Private Function PresetRow(name As String) As Control
        Return tabs.Controls.Find(name, True).Single().Parent
    End Function

    Private Sub Borrow(moved As Control, destination As Control, Optional simpleCaption As String = Nothing)
        If moved Is Nothing OrElse moved.Parent Is Nothing Then Throw New InvalidOperationException("Quick start control without a place: " & destination.Name)
        If simpleCaption IsNot Nothing AndAlso Not TypeOf moved Is SettingRow Then Throw New InvalidOperationException("Only settings rows take a Quick start caption.")
        viewPlacements.Add(New ViewPlacement With {.Moved = moved, .FullParent = moved.Parent, .FullIndex = moved.Parent.Controls.GetChildIndex(moved),
                                                   .SimpleParent = destination, .SimpleCaption = simpleCaption, .FullCaption = TryCast(moved, SettingRow)?.CaptionText})
    End Sub

    Private Sub ViewPickerChanged(sender As Object, e As EventArgs)
        If restoringView Then Return
        If viewPicker.Advanced AndAlso SimpleViewShown AndAlso Not ConfirmAdvancedView.Invoke() Then
            restoringView = True
            Try
                viewPicker.Advanced = False ' Cancel keeps Quick start, every edit and the saved view.
            Finally
                restoringView = False
            End Try
            Return
        End If
        ApplyView(viewPicker.Advanced)
        If IsHandleCreated Then RefreshState()
        SaveWindowPreferences()
    End Sub

    Private Function AskAdvancedView() As Boolean
        Using dialog As New AdvancedViewConfirmationForm()
            Return dialog.ShowDialog(Me) = DialogResult.OK
        End Using
    End Function

    ' Switches to Advanced (after the confirmation) and opens one of its tabs.
    Private Sub ShowAdvancedTab(title As String)
        viewPicker.Advanced = True
        If Not SimpleViewShown Then tabs.SelectedTab = fullPages.Single(Function(candidate) candidate.Text = title)
    End Sub

    Private Sub ApplyView(advanced As Boolean)
        If quickPage Is Nothing OrElse advanced <> SimpleViewShown Then Return
        SuspendLayout() : tabs.SuspendLayout()
        Try
            If Not advanced Then
                If tabs.SelectedTab IsNot Nothing Then lastFullPage = tabs.SelectedTab
                For Each group In viewPlacements.GroupBy(Function(placement) placement.SimpleParent)
                    ' Borrowed controls follow the section heading, before Quick start's own notes.
                    Dim heading = group.Key.Controls.OfType(Of SectionHeader)().FirstOrDefault()
                    Dim index = If(heading Is Nothing, 0, group.Key.Controls.GetChildIndex(heading) + 1)
                    For Each placement In group
                        If placement.SimpleCaption IsNot Nothing Then DirectCast(placement.Moved, SettingRow).CaptionText = placement.SimpleCaption
                        group.Key.Controls.Add(placement.Moved)
                        group.Key.Controls.SetChildIndex(placement.Moved, index)
                        index += 1
                    Next
                Next
                tabs.TabPages.Clear() : tabs.TabPages.Add(quickPage)
                LauncherAppearance.Apply(quickPage)
            Else
                For Each group In viewPlacements.GroupBy(Function(placement) placement.FullParent)
                    For Each placement In group.OrderBy(Function(item) item.FullIndex)
                        If placement.SimpleCaption IsNot Nothing Then DirectCast(placement.Moved, SettingRow).CaptionText = placement.FullCaption
                        group.Key.Controls.Add(placement.Moved)
                        group.Key.Controls.SetChildIndex(placement.Moved, placement.FullIndex)
                    Next
                Next
                tabs.TabPages.Clear() : tabs.TabPages.AddRange(fullPages)
                For Each restored In fullPages
                    LauncherAppearance.Apply(restored)
                Next
                tabs.SelectedTab = lastFullPage
            End If
        Finally
            tabs.ResumeLayout(True) : ResumeLayout(True)
        End Try
        For Each shown As TabPage In tabs.TabPages
            For Each pageContent As Control In shown.Controls
                pageContent.PerformLayout() : RefreshLabelWidths(pageContent)
            Next
        Next
    End Sub

    ' Getting started points here; this topic explains the two views.
    Private Shared Function ViewsTopic() As HelpTopic
        Return New HelpTopic(HelpViews,
            "The Simple | Advanced switch beside ? chooses how much of the launcher you see. Advanced, the default, shows every option on the Launcher, Graphics, Controls, Settings and Advanced tabs." & vbCrLf & vbCrLf &
            "Simple shows one Quick start page with the essentials: Launch mode with direct practice, startup logo skipping, experimental VR and the VR runtime, the desktop and VR quality presets, VR render scale and the quick VR presets, and recentering. Settings profiles… opens them in Advanced." & vbCrLf & vbCrLf &
            "Both views use the same controls, so a choice made in one shows in the other. Switching views never changes or saves settings; Save settings and Launch work as usual. Switching to Advanced asks first, and DiRT3VR remembers the view with the window size.")
    End Function
End Class

' The Simple | Advanced switch in the launcher's header.
Public NotInheritable Class LauncherViewPicker
    Inherits FlowLayoutPanel
    Private ReadOnly simpleChoice As New RadioButton With {.Name = "SimpleView", .Text = "Simple", .Appearance = Appearance.Button, .AutoSize = True, .Margin = New Padding(0), .TextAlign = ContentAlignment.MiddleCenter, .FlatStyle = FlatStyle.Flat}
    Private ReadOnly advancedChoice As New RadioButton With {.Name = "AdvancedView", .Text = "Advanced", .Appearance = Appearance.Button, .AutoSize = True, .Margin = New Padding(0), .TextAlign = ContentAlignment.MiddleCenter, .FlatStyle = FlatStyle.Flat, .Checked = True}
    Public Event ViewChanged As EventHandler

    Public Sub New()
        Name = "LauncherView" : AccessibleName = "Simple or Advanced launcher view"
        AutoSize = True : AutoSizeMode = AutoSizeMode.GrowAndShrink : WrapContents = False : Margin = New Padding(0, 4, 8, 0)
        Controls.AddRange({simpleChoice, advancedChoice})
        AddHandler advancedChoice.CheckedChanged, Sub() RaiseEvent ViewChanged(Me, EventArgs.Empty)
        AddHandler BackColorChanged, Sub() UpdateColors()
        UpdateColors()
    End Sub

    <System.ComponentModel.Browsable(False), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property Advanced As Boolean
        Get
            Return advancedChoice.Checked
        End Get
        Set(value As Boolean)
            If value Then advancedChoice.Checked = True Else simpleChoice.Checked = True
        End Set
    End Property

    ' The chosen view reads as pressed in both themes.
    Private Sub UpdateColors()
        For Each choice In {simpleChoice, advancedChoice}
            choice.Padding = New Padding(Px(Me, 8), Px(Me, 2), Px(Me, 8), Px(Me, 2))
            choice.FlatAppearance.BorderColor = LauncherAppearance.Line
            choice.FlatAppearance.CheckedBackColor = If(LauncherAppearance.Dark, Color.FromArgb(80, 72, 40), Color.FromArgb(255, 230, 150))
            choice.FlatAppearance.MouseOverBackColor = If(LauncherAppearance.Dark, Color.FromArgb(64, 60, 42), Color.FromArgb(255, 243, 203))
        Next
    End Sub
End Class

' Asked before Simple switches to Advanced (GRID2VR asks the same).
Public NotInheritable Class AdvancedViewConfirmationForm
    Inherits LauncherForm

    Public Sub New()
        Text = "Switch to Advanced view" : Font = New Font("Segoe UI", 10)
        AutoScaleMode = AutoScaleMode.Dpi : FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent : ShowInTaskbar = False : MinimizeBox = False : MaximizeBox = False
        AutoSize = True : AutoSizeMode = AutoSizeMode.GrowAndShrink
        Dim body As New TableLayoutPanel With {.Name = "AdvancedViewContent", .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Padding = New Padding(Px(Me, 20)), .ColumnCount = 1, .RowCount = 2}
        Dim message As New Label With {.Name = "AdvancedViewMessage", .AutoSize = True, .UseMnemonic = False, .MaximumSize = New Size(Px(Me, 420), 0),
                                       .Text = "Advanced shows every option on separate tabs." & vbLf & "Your settings stay as they are."}
        Dim actions As New FlowLayoutPanel With {.Name = "AdvancedViewActions", .AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .WrapContents = False, .Anchor = AnchorStyles.Right, .Margin = New Padding(0, Px(Me, 16), 0, 0)}
        Dim cancel As New Button With {.Name = "CancelAdvancedView", .Text = "Cancel", .DialogResult = DialogResult.Cancel, .AutoSize = True}
        Dim approve As New Button With {.Name = "ApproveAdvancedView", .Text = "Show Advanced", .DialogResult = DialogResult.OK, .AutoSize = True}
        actions.Controls.AddRange({cancel, approve})
        body.Controls.Add(message, 0, 0) : body.Controls.Add(actions, 0, 1)
        Controls.Add(body)
        AcceptButton = approve : CancelButton = cancel : ActiveControl = cancel
    End Sub
End Class
