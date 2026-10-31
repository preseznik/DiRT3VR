Imports System.Drawing
Imports System.Windows.Forms

' Asks before turning an OpenXR add-on off for DiRT 3 (adapted from DiRT2VR).
Public NotInheritable Class VrLayerRecoveryForm
    Inherits LauncherForm

    Private ReadOnly remember As New CheckBox With {.Name = "RememberLayerBypass", .Text = "Remember for this DiRT3VR installation", .AutoSize = True}

    Public ReadOnly Property RememberChoice As Boolean
        Get
            Return remember.Checked
        End Get
    End Property

    Public Sub New(request As VrLayerRecoveryRequest)
        Text = "VR compatibility"
        Font = New Font("Segoe UI", 10) : AutoScaleMode = AutoScaleMode.Dpi
        AutoSize = True : AutoSizeMode = AutoSizeMode.GrowAndShrink : AutoScroll = True
        MaximumSize = Screen.PrimaryScreen.WorkingArea.Size
        FormBorderStyle = FormBorderStyle.FixedDialog : StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False : MaximizeBox = False
        Dim layout As New TableLayoutPanel With {.AutoSize = True, .Padding = New Padding(20), .ColumnCount = 1}
        layout.Controls.Add(New Label With {.Name = "LayerFailureTitle", .Text = "An OpenXR add-on is stopping VR from starting", .AutoSize = True, .MaximumSize = New Size(560, 0), .Font = New Font(Font, FontStyle.Bold)})
        Dim description = String.Join(Environment.NewLine & Environment.NewLine, request.Layers.Select(Function(layer) layer.DisplayName & ": " & layer.Consequence))
        layout.Controls.Add(New Label With {.Name = "LayerConsequences", .Text = description, .AutoSize = True, .MaximumSize = New Size(560, 0), .Margin = New Padding(3, 12, 3, 12), .UseMnemonic = False})
        layout.Controls.Add(New Label With {.Text = "Turn it off for DiRT 3 and try again? Other apps keep using it, and nothing is uninstalled.", .AutoSize = True, .MaximumSize = New Size(560, 0)})
        remember.Margin = New Padding(3, 12, 3, 6)
        layout.Controls.Add(remember)
        layout.Controls.Add(New Label With {.Text = "You can turn remembered add-ons back on in Settings → VR runtime → OpenXR add-ons.", .AutoSize = True, .MaximumSize = New Size(560, 0)})
        Dim details As New TextBox With {.Name = "LayerFailureDetails", .Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Both, .WordWrap = False, .Width = 560, .Height = 150, .Visible = False, .Text = request.Report}
        layout.Controls.Add(details)
        Dim buttons As New FlowLayoutPanel With {.AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Dock = DockStyle.Fill, .Margin = New Padding(0, 16, 0, 0)}
        Dim retry As New Button With {.Name = "RetryWithoutLayers", .Text = "Disable and retry", .AutoSize = True, .DialogResult = DialogResult.OK}
        Dim cancel As New Button With {.Name = "CancelLayerRecovery", .Text = "Cancel", .AutoSize = True, .DialogResult = DialogResult.Cancel}
        Dim show As New Button With {.Name = "ShowLayerDetails", .Text = "Show details", .AutoSize = True}
        AddHandler show.Click, Sub()
                                   details.Visible = Not details.Visible
                                   show.Text = If(details.Visible, "Hide details", "Show details")
                               End Sub
        buttons.Controls.AddRange({retry, cancel, show}) : layout.Controls.Add(buttons)
        Controls.Add(layout)
        ' Nothing changes unless the player picks Disable and retry.
        AcceptButton = cancel : CancelButton = cancel
    End Sub

    Public Shared Function Confirm(request As VrLayerRecoveryRequest, owner As IWin32Window) As VrLayerRecoveryChoice
        Using dialog As New VrLayerRecoveryForm(request)
            Return New VrLayerRecoveryChoice With {.Retry = dialog.ShowDialog(owner) = DialogResult.OK, .Remember = dialog.RememberChoice}
        End Using
    End Function
End Class

' Lists remembered add-on bypasses so they can be turned back on.
Public NotInheritable Class VrCompatibilityForm
    Inherits LauncherForm

    Private ReadOnly context As InstallContext
    Private ReadOnly layers As New ListBox With {.Name = "RememberedLayerBypasses", .Dock = DockStyle.Fill, .IntegralHeight = False, .HorizontalScrollbar = True}
    Private ReadOnly summary As New Label With {.Name = "LayerBypassSummary", .AutoSize = True, .MaximumSize = New Size(520, 0)}
    Private ReadOnly reenable As New Button With {.Name = "ReenableLayer", .Text = "Turn back on", .AutoSize = True}

    Public Sub New(context As InstallContext)
        Me.context = context
        Text = "OpenXR add-ons"
        Font = New Font("Segoe UI", 10) : AutoScaleMode = AutoScaleMode.Dpi
        ClientSize = New Size(580, 340) : MinimumSize = New Size(460, 300)
        StartPosition = FormStartPosition.CenterParent : MinimizeBox = False : MaximizeBox = False
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(16), .ColumnCount = 1, .RowCount = 4}
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize)) : layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize)) : layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.Controls.Add(summary, 0, 0)
        layout.Controls.Add(layers, 0, 1)
        layout.Controls.Add(New Label With {.Text = "Changes apply to the next VR launch. Other apps are unaffected. If an add-on's registration changes, DiRT3VR asks again.", .AutoSize = True, .MaximumSize = New Size(520, 0)}, 0, 2)
        Dim buttons As New FlowLayoutPanel With {.AutoSize = True, .FlowDirection = FlowDirection.RightToLeft, .Dock = DockStyle.Fill, .Margin = New Padding(0, 12, 0, 0)}
        Dim closeButton As New Button With {.Text = "Close", .AutoSize = True, .DialogResult = DialogResult.Cancel}
        buttons.Controls.AddRange({closeButton, reenable}) : layout.Controls.Add(buttons, 0, 3)
        AddHandler layers.SelectedIndexChanged, Sub() reenable.Enabled = layers.SelectedItem IsNot Nothing
        AddHandler reenable.Click, Sub() TurnBackOn()
        Controls.Add(layout) : CancelButton = closeButton
        RefreshLayers()
    End Sub

    ' Public for the launcher tests.
    Public Sub TurnBackOn()
        Dim selected = TryCast(layers.SelectedItem, VrLayerBypass)
        If selected Is Nothing Then Return
        Try
            VrLayerPreferences.Reenable(context, selected.LayerName)
            RefreshLayers()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            summary.Text = "Could not change the remembered choice: " & ex.Message
        End Try
    End Sub

    Private Sub RefreshLayers()
        layers.Items.Clear()
        layers.Items.AddRange(VrLayerPreferences.Load(context).Bypasses.Cast(Of Object)().ToArray())
        summary.Text = If(layers.Items.Count = 0, "No OpenXR add-ons are turned off for DiRT 3.", "Turned off for DiRT 3 by remembered choices:")
        reenable.Enabled = False
        If layers.Items.Count > 0 Then layers.SelectedIndex = 0
    End Sub
End Class
