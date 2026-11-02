Imports DiRT3VR
Imports System.IO
Imports System.Text.Json
Imports System.Windows.Forms

Module SimpleViewTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function Open(ctx As InstallContext) As MainForm
        Dim form As New MainForm(ctx) With {.ShowInTaskbar = False, .StartPosition = FormStartPosition.Manual, .Location = New Drawing.Point(-30000, -30000)}
        form.Show() : Application.DoEvents()
        Return form
    End Function

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "simple-view")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .VrRenderScale = 120}.Save(ctx)
        Dim pageOf = Function(owner As MainForm, name As String) As TabPage
                         Dim found As Control = owner.Controls.Find(name, True).Single()
                         While found IsNot Nothing AndAlso Not TypeOf found Is TabPage
                             found = found.Parent
                         End While
                         Return DirectCast(found, TabPage)
                     End Function
        Dim borrowed = {"LaunchModeDirect", "DirectRoute", "SkipStartupMovies", "ExperimentalVR", "RuntimeChoice", "QualityPreset", "VrQualityPreset", "VrRenderScale", "PotatoPreset", "VrResolutionSummary", "LastVrSession", "RecenterKey", "RecenterDevice", "RecenterKeepLevel"}
        Using form = Open(ctx)
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            Dim simple = DirectCast(form.Controls.Find("SimpleView", True).Single(), RadioButton)
            Dim advanced = DirectCast(form.Controls.Find("AdvancedView", True).Single(), RadioButton)
            Dim fullTitles = tabs.TabPages.Cast(Of TabPage)().Select(Function(page) page.Text).ToArray()
            check(advanced.Checked AndAlso Not form.SimpleViewShown AndAlso fullTitles.SequenceEqual({"Launcher", "Graphics", "Controls", "Settings", "Advanced"}), "Advanced, with every tab, is the default view")
            Dim home = borrowed.ToDictionary(Function(name) name, Function(name) pageOf(form, name).Text)
            Dim rowIndex = Function(name As String)
                               Dim row = form.Controls.Find(name, True).Single().Parent
                               Return (row.Parent, row.Parent.Controls.GetChildIndex(row))
                           End Function
            Dim scaleBefore = rowIndex("VrRenderScale"), runtimeBefore = rowIndex("RuntimeChoice")
            Dim before = JsonSerializer.Serialize(form.CurrentSettings())
            tabs.SelectedIndex = 1 : Application.DoEvents()

            simple.Checked = True : Application.DoEvents()
            Dim quick = tabs.TabPages.Cast(Of TabPage)().ToArray()
            check(form.SimpleViewShown AndAlso quick.Length = 1 AndAlso quick(0).Text = "Quick start", "Simple shows one Quick start page instead of the tabs")
            Dim missing = borrowed.Where(Function(name) quick(0).Controls.Find(name, True).Length <> 1).ToArray()
            check(missing.Length = 0, "Quick start holds launch mode with direct practice, startup, VR and runtime, quality presets, render scale, quick presets, last session and recentering (missing: " & String.Join(", ", missing) & ")")
            check(form.Controls.Find("VrRenderScale", True).Length = 1 AndAlso form.Controls.Find("LaunchModeDirect", True).Length = 1, "controls are moved, not duplicated")
            check(form.Controls.Find("QualityPresetLabel", True).Single().Text = "Desktop quality" AndAlso form.Controls.Find("VrQualityPresetLabel", True).Single().Text = "VR quality", "the two preset rows say which is which")
            check(quick(0).Controls.Find("InstructionsLink", True).OfType(Of LinkLabel)().Any(Function(link) Object.Equals(link.Tag, MainForm.HelpViews)) AndAlso form.HelpTopics().Any(Function(topic) topic.Title = MainForm.HelpViews),
                  "Quick start links its own help topic")
            check(JsonSerializer.Serialize(form.CurrentSettings()) = before AndAlso Not form.HasUnsavedSettings(), "switching to Simple changes no setting")
            Dim status = form.Controls.Find("SessionStatus", True).Single().Text
            check(status.Contains("under VR headset") AndAlso Not status.Contains("in Advanced"), "the VR opt-in hint points to Quick start in Simple (" & status & ")")
            Dim quickTitles = form.SectionHeaders.Select(Function(header) header.Title).ToArray()
            check(quickTitles.Contains("VR headset") AndAlso quickTitles.Contains("Performance") AndAlso quickTitles.Contains("Controls & comfort") AndAlso quickTitles.Distinct().Count() = quickTitles.Length AndAlso
                  form.SectionHeaders.All(Function(header) Not header.Collapsed), "Quick start's sections are collapsible like the others and start open")

            ' A change in Simple shows in Advanced.
            DirectCast(form.Controls.Find("LaunchModeDirect", True).Single(), RadioButton).Checked = True
            DirectCast(form.Controls.Find("VrRenderScale", True).Single(), ValueSlider).Value = 140
            Application.DoEvents()
            Dim edited = JsonSerializer.Serialize(form.CurrentSettings())
            Dim asked = 0
            form.ConfirmAdvancedView = Function()
                                           asked += 1
                                           Return False
                                       End Function
            advanced.Checked = True : Application.DoEvents()
            check(asked = 1 AndAlso form.SimpleViewShown AndAlso simple.Checked AndAlso JsonSerializer.Serialize(form.CurrentSettings()) = edited, "Cancel on the confirmation keeps Simple and every edit")
            form.ConfirmAdvancedView = Function()
                                           asked += 1
                                           Return True
                                       End Function
            advanced.Checked = True : Application.DoEvents()
            check(asked = 2 AndAlso Not form.SimpleViewShown AndAlso tabs.TabPages.Cast(Of TabPage)().Select(Function(page) page.Text).SequenceEqual(fullTitles) AndAlso tabs.SelectedIndex = 1,
                  "confirming shows every tab again, on the tab that was open")
            Dim moved = borrowed.Where(Function(name) pageOf(form, name).Text <> home(name)).ToArray()
            check(moved.Length = 0, "every borrowed control is back on its own tab (" & String.Join(", ", moved) & ")")
            check(rowIndex("VrRenderScale").Equals(scaleBefore) AndAlso rowIndex("RuntimeChoice").Equals(runtimeBefore), "and in its own place")
            check(form.Controls.Find("QualityPresetLabel", True).Single().Text = "Preset" AndAlso form.Controls.Find("VrQualityPresetLabel", True).Single().Text = "Preset", "the preset rows get their own labels back")
            check(JsonSerializer.Serialize(form.CurrentSettings()) = edited AndAlso form.CurrentSettings().LaunchMode = "direct" AndAlso form.CurrentSettings().VrRenderScale = 140 AndAlso form.HasUnsavedSettings(),
                  "the edits made in Simple stay, unsaved, in Advanced")

            ' Simple is remembered with the window; the profiles shortcut opens Advanced → Settings.
            simple.Checked = True : Application.DoEvents()
            form.Close()
        End Using
        Using reopened = Open(ctx)
            Dim tabs = DirectCast(reopened.Controls.Find("LauncherTabs", True).Single(), TabControl)
            check(reopened.SimpleViewShown AndAlso DirectCast(reopened.Controls.Find("SimpleView", True).Single(), RadioButton).Checked, "the launcher reopens in Simple")
            check(reopened.CurrentSettings().VrRenderScale = 120 AndAlso reopened.CurrentSettings().LaunchMode = "normal", "closing saved the view, not the unsaved edits")
            check(reopened.Controls.Find("SimpleDrivingControls", True).Single().Text = "Driving controls…", "Simple's Controls & comfort opens the driving controls too")
            Dim asked = 0
            reopened.ConfirmAdvancedView = Function()
                                               asked += 1
                                               Return True
                                           End Function
            DirectCast(reopened.Controls.Find("SimpleProfiles", True).Single(), Button).PerformClick() : Application.DoEvents()
            check(asked = 1 AndAlso Not reopened.SimpleViewShown AndAlso tabs.SelectedTab.Text = "Settings", "Settings profiles… asks, then opens Advanced on the Settings tab")
            reopened.Close()
        End Using
        Using third = Open(ctx)
            check(Not third.SimpleViewShown, "Advanced is remembered too")
            third.Close()
        End Using

        ' The confirmation dialog: Cancel is the safe default.
        Using dialog As New AdvancedViewConfirmationForm()
            dialog.ShowInTaskbar = False : dialog.StartPosition = FormStartPosition.Manual : dialog.Location = New Drawing.Point(-30000, -30000)
            dialog.Show() : Application.DoEvents()
            Dim cancel = DirectCast(dialog.Controls.Find("CancelAdvancedView", True).Single(), Button)
            Dim approve = DirectCast(dialog.Controls.Find("ApproveAdvancedView", True).Single(), Button)
            check(dialog.CancelButton Is cancel AndAlso cancel.DialogResult = DialogResult.Cancel AndAlso approve.DialogResult = DialogResult.OK AndAlso dialog.ActiveControl Is cancel, "the confirmation focuses Cancel")
            Dim cancelBounds = dialog.RectangleToClient(cancel.Parent.RectangleToScreen(cancel.Bounds))
            Dim approveBounds = dialog.RectangleToClient(approve.Parent.RectangleToScreen(approve.Bounds))
            check(cancelBounds.Bottom <= dialog.ClientSize.Height AndAlso approveBounds.Right <= dialog.ClientSize.Width AndAlso approveBounds.Left >= 0 AndAlso dialog.Controls.Find("AdvancedViewMessage", True).Single().Text.Contains("settings stay"), "it fits its buttons and says settings stay")
            dialog.Close()
        End Using
    End Sub
End Module
