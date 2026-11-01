Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

Module StatusTextTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        ' Copyable status text (adapted from GRID2VR's SelectableStatusText checks).
        Using host As New Form With {.ShowInTaskbar = False, .StartPosition = FormStartPosition.Manual, .Location = New Point(-30000, -30000), .Font = New Font("Segoe UI", 10)}
            ' In the launcher a table cell sets the width; standing alone it must not be anchored to the form.
            Dim status As New SelectableStatusText With {.Anchor = AnchorStyles.Top Or AnchorStyles.Left}
            host.Controls.Add(status)
            host.Show() : Application.DoEvents()
            Dim longText = String.Join(" ", Enumerable.Repeat("DiRT 3 exited before its game window appeared (exit code 0xC0000005). Open logs for details.", 12))
            status.Text = longText : status.FitWidth(460) : Application.DoEvents()
            Dim line = status.Font.Height
            check(status.ReadOnly AndAlso status.TabStop AndAlso status.ShortcutsEnabled AndAlso status.Width = 460, $"status text is read-only, reachable with Tab and copyable (readOnly={status.ReadOnly}, tabStop={status.TabStop}, shortcuts={status.ShortcutsEnabled}, width={status.Width})")
            check(status.Height > line * 2 AndAlso status.Height <= line * 6 + Px(status, 2) + 2 AndAlso status.ScrollBars = ScrollBars.Vertical, "long status text wraps to at most six lines, then scrolls (" & status.Height & " px)")
            status.SelectAll()
            check(status.SelectedText = longText, "the whole message can be selected for copying")
            status.Select(4, 10) : status.Text = longText
            check(status.SelectionStart = 4 AndAlso status.SelectionLength = 10, "refreshing the same message keeps the selection")
            status.Text = "Ready." : status.FitWidth(900)
            check(status.Height < line * 2 AndAlso status.ScrollBars = ScrollBars.None, "a short message takes one line (" & status.Height & " px)")
            status.Text = ""
            check(status.Height = 0, "an empty message takes no space")
            host.Close()
        End Using

        ' Hover reasons for disabled buttons (adapted from DiRT2VR's LaunchButtonTips).
        Using host As New Form With {.ShowInTaskbar = False, .StartPosition = FormStartPosition.Manual, .Location = New Point(100, 100), .ClientSize = New Size(400, 200)}, tips As New ToolTip With {.InitialDelay = 500}
            Dim button As New Button With {.Text = "Launch VR", .Location = New Point(20, 20), .Size = New Size(120, 34), .Enabled = False}
            host.Controls.Add(button)
            host.Show() : Application.DoEvents()
            Using hover As New LaunchButtonTips(host, tips)
                hover.Update(button, "Start VR.", "Enable experimental VR in Advanced first.")
                check(tips.GetToolTip(button) = "Enable experimental VR in Advanced first." AndAlso button.AccessibleDescription = "Enable experimental VR in Advanced first.", "a disabled button's tooltip and accessible description give the reason")
                Dim inside = button.RectangleToScreen(button.ClientRectangle).Location + New Size(5, 5)
                hover.Poll(inside, True, 1000)
                check(hover.ShownReason Is Nothing, "the reason waits for the usual tooltip delay")
                hover.Poll(inside, True, 1600)
                check(hover.ShownReason = "Enable experimental VR in Advanced first.", "resting on the disabled button shows why it is disabled")
                hover.Poll(New Point(0, 0), True, 1700)
                check(hover.ShownReason Is Nothing, "moving away hides it")
                hover.Poll(inside, False, 1800) : hover.Poll(inside, False, 3000)
                check(hover.ShownReason Is Nothing, "nothing shows while another window is in front")
                button.Enabled = True
                hover.Update(button, "Start VR.", "Enable experimental VR in Advanced first.")
                check(tips.GetToolTip(button) = "Start VR." AndAlso hover.Reason(button) = "", "an enabled button keeps its usual tooltip")
                hover.Poll(inside, True, 4000) : hover.Poll(inside, True, 5000)
                check(hover.ShownReason Is Nothing, "enabled buttons use their ordinary tooltip instead")
            End Using
            host.Close()
        End Using

        ' In the launcher: status lines are copyable, disabled launch buttons explain themselves, and
        ' diagnostic logging has a place under Settings → Diagnostics.
        Dim ctx = Context(root, "status-ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(TypeOf form.Controls.Find("SessionStatus", True).Single() Is SelectableStatusText AndAlso TypeOf form.Controls.Find("Notice", True).Single() Is SelectableStatusText, "the status line and messages can be selected and copied")
            Dim vr = form.Controls.Find("LaunchVR", True).Single()
            Dim expected = RuntimeCapabilities.ExperimentalReason(ctx, form.CurrentSettings())
            check(Not vr.Enabled AndAlso expected <> "" AndAlso vr.AccessibleDescription = expected, "disabled Launch VR explains why (" & vr.AccessibleDescription & ")")
            Dim restore = form.Controls.Find("RestoreFiles", True).Single()
            check(restore.Enabled AndAlso restore.AccessibleDescription.StartsWith("Restore the game files", StringComparison.Ordinal), "enabled buttons keep their usual description")
            Dim logging = DirectCast(form.Controls.Find("DiagnosticLogging", True).Single(), CheckBox)
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            Dim settingsPage = tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Text = "Settings")
            check(logging.Parent IsNot Nothing AndAlso settingsPage.Contains(logging), "Diagnostic logging is on the Settings tab")
            tabs.SelectedTab = settingsPage : Application.DoEvents()
            logging.Checked = Not logging.Checked
            check(form.CurrentSettings().DiagnosticLogging = logging.Checked AndAlso form.HasUnsavedSettings(), "the Diagnostic logging checkbox changes the launcher setting")
            form.Close()
        End Using
    End Sub
End Module
