Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module HelpTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "help-ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .RecenterKey = 120, .RecenterModifiers = 1}.Save(ctx)
        Dim topics As IReadOnlyList(Of HelpTopic)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            topics = form.HelpTopics()
            Dim titles = topics.Select(Function(topic) topic.Title).ToArray()
            check(titles.Length >= 10 AndAlso titles.Distinct(StringComparer.OrdinalIgnoreCase).Count() = titles.Length AndAlso titles(0) = MainForm.HelpStart, "the instructions are split into distinct topics, starting with Getting started")
            For Each expected In {MainForm.HelpStartup, MainForm.HelpVr, MainForm.HelpSession, MainForm.HelpGraphics, MainForm.HelpControls, MainForm.HelpRuntime, MainForm.HelpSettings, MainForm.HelpRestore, MainForm.HelpUpdates, MainForm.HelpFiles, MainForm.HelpAbout}
                check(titles.Contains(expected), "there is a help topic for " & expected)
            Next
            Dim body = Function(title As String) topics.Single(Function(topic) topic.Title = title).Body
            check(body(MainForm.HelpControls).Contains(RecenterShortcut.Describe(120, 1)), "the recentering topic names the key chosen in this launcher (" & RecenterShortcut.Describe(120, 1) & ")")
            check(body(MainForm.HelpFiles).Contains(ctx.UserRoot), "the files topic names this installation's preferences folder")
            check(body(MainForm.HelpStart).Contains(BuildInfo.Version) OrElse body(MainForm.HelpStart).Contains(GetType(MainForm).Assembly.GetName().Version.ToString(3)), "Getting started shows the version")
            check(body(MainForm.HelpAbout).Contains("https://github.com/preseznik/DiRT3VR"), "About links the project")

            ' Every tab has an Instructions link, and every link opens an existing topic.
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            For Each page As TabPage In tabs.TabPages
                Dim links = page.Controls.Find("InstructionsLink", True).OfType(Of LinkLabel)().ToArray()
                check(links.Length >= 1, "the " & page.Text & " tab has an Instructions link")
                For Each link In links
                    Dim topic = TryCast(link.Tag, String)
                    check(topic IsNot Nothing AndAlso titles.Contains(topic) AndAlso link.AccessibleName = "Instructions: " & topic, "the Instructions link on " & page.Text & " opens its own topic (" & topic & ")")
                Next
            Next
            form.Close()
        End Using

        ' The About window shows the topics beside the article, or as a dropdown when narrow.
        Using about As New AboutForm(ctx, topics, initialTopic:=MainForm.HelpGraphics)
            about.ShowInTaskbar = False : about.StartPosition = FormStartPosition.Manual : about.Location = New Drawing.Point(-30000, -30000)
            about.Show() : Application.DoEvents()
            Dim view = about.Instructions
            Dim list = DirectCast(about.Controls.Find("InstructionTopics", True).Single(), ListBox)
            Dim picker = DirectCast(about.Controls.Find("InstructionTopicPicker", True).Single(), ComboBox)
            check(view.SelectedTopic = MainForm.HelpGraphics AndAlso view.ArticleText.StartsWith(MainForm.HelpGraphics & vbCrLf, StringComparison.Ordinal) AndAlso view.ArticleText.Contains("render scale"), "a section's link opens its topic")
            check(Not view.Compact AndAlso list.Visible AndAlso Not picker.Visible AndAlso list.Items.Count = topics.Count, "the topic list sits beside the article in a wide window")
            list.SelectedIndex = topics.ToList().FindIndex(Function(topic) topic.Title = MainForm.HelpRestore)
            Application.DoEvents()
            check(view.SelectedTopic = MainForm.HelpRestore AndAlso view.ArticleText.Contains("recovery.json") AndAlso picker.SelectedIndex = list.SelectedIndex, "choosing a topic in the list shows it")
            view.ShowTopic("No such topic")
            check(view.SelectedTopic = MainForm.HelpStart, "an unknown topic opens Getting started")
            about.Width = 540 : Application.DoEvents() : about.PerformLayout() : Application.DoEvents()
            check(view.Compact AndAlso picker.Visible AndAlso Not list.Visible, "a narrow window offers the topics as a dropdown (view width " & view.Width & ")")
            picker.SelectedIndex = topics.ToList().FindIndex(Function(topic) topic.Title = MainForm.HelpSession)
            Application.DoEvents()
            check(view.SelectedTopic = MainForm.HelpSession AndAlso view.ArticleText.Contains("notification area"), "choosing a topic in the dropdown shows it")
            about.Width = 900 : Application.DoEvents() : about.PerformLayout() : Application.DoEvents()
            check(Not view.Compact AndAlso list.Visible AndAlso list.SelectedIndex = picker.SelectedIndex, "widening the window brings the list back on the same topic")
            about.Close()
        End Using
        check(Throws(Sub() Call New HelpTopic("", "text").ToString()) AndAlso Throws(Sub() Call New InstructionsView(Array.Empty(Of HelpTopic)()).Dispose()), "empty topics are refused")
    End Sub

    Private Function Throws(action As Action) As Boolean
        Try
            action()
        Catch ex As ArgumentException
            Return True
        End Try
        Return False
    End Function
End Module
