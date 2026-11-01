Imports System.Drawing
Imports System.Windows.Forms

' One page of the launcher instructions.
Public NotInheritable Class HelpTopic
    Public ReadOnly Property Title As String
    Public ReadOnly Property Body As String

    Public Sub New(heading As String, content As String)
        If String.IsNullOrWhiteSpace(heading) OrElse String.IsNullOrWhiteSpace(content) Then Throw New ArgumentException("A help topic needs a title and text.")
        _Title = heading : _Body = content
    End Sub

    Public Overrides Function ToString() As String
        Return Title
    End Function
End Class

' Instructions as topics (adapted from DiRT2VR's InstructionsView): a topic list beside the article,
' or a topic picker above it when there isn't room for both.
Public NotInheritable Class InstructionsView
    Inherits UserControl

    Private ReadOnly topicList As New ListBox With {.Name = "InstructionTopics", .Dock = DockStyle.Left, .BorderStyle = BorderStyle.None, .IntegralHeight = False, .AccessibleName = "Instruction topics"}
    Private ReadOnly picker As New ComboBox With {.Name = "InstructionTopicPicker", .Dock = DockStyle.Top, .DropDownStyle = ComboBoxStyle.DropDownList, .Visible = False, .AccessibleName = "Instruction topic"}
    Private ReadOnly listGap As New Panel With {.Dock = DockStyle.Left}
    Private ReadOnly pickerGap As New Panel With {.Dock = DockStyle.Top, .Visible = False}
    Private ReadOnly article As New TextBox With {.Name = "Instructions", .ReadOnly = True, .Multiline = True, .ScrollBars = ScrollBars.Vertical, .Dock = DockStyle.Fill, .BorderStyle = BorderStyle.None, .AccessibleName = "Instructions"}
    Private ReadOnly topics As IReadOnlyList(Of HelpTopic)
    Private updating As Boolean
    Private isCompact As Boolean

    Public Sub New(items As IReadOnlyList(Of HelpTopic), Optional initialTopic As String = Nothing)
        If items Is Nothing OrElse items.Count = 0 Then Throw New ArgumentException("At least one help topic is required.")
        topics = items
        Name = "InstructionsView"
        ' Docking runs from the last control added: picker and list first, the article fills the rest.
        Controls.Add(article) : Controls.Add(listGap) : Controls.Add(topicList) : Controls.Add(pickerGap) : Controls.Add(picker)
        For Each item In topics
            topicList.Items.Add(item) : picker.Items.Add(item)
        Next
        StyleChoice(picker, Me)
        AddHandler topicList.SelectedIndexChanged, Sub() Choose(topicList.SelectedIndex)
        AddHandler picker.SelectedIndexChanged, Sub() Choose(picker.SelectedIndex)
        ShowTopic(initialTopic)
    End Sub

    Public ReadOnly Property SelectedTopic As String
        Get
            Return topics(Math.Max(0, topicList.SelectedIndex)).Title
        End Get
    End Property

    Public ReadOnly Property ArticleText As String
        Get
            Return article.Text
        End Get
    End Property

    ' True when the topics are offered as a dropdown above the article instead of a list beside it.
    Public ReadOnly Property Compact As Boolean
        Get
            Return isCompact
        End Get
    End Property

    ' Opens the named topic; an unknown or missing name opens the first one.
    Public Sub ShowTopic(heading As String)
        Dim index = 0
        For i = 0 To topics.Count - 1
            If String.Equals(topics(i).Title, heading, StringComparison.OrdinalIgnoreCase) Then index = i : Exit For
        Next
        Choose(index)
    End Sub

    Private Sub Choose(index As Integer)
        If updating OrElse index < 0 OrElse index >= topics.Count Then Return
        updating = True
        Try
            topicList.SelectedIndex = index : picker.SelectedIndex = index
            article.Text = topics(index).Title & vbCrLf & vbCrLf & topics(index).Body
            article.AccessibleDescription = topics(index).Title
            article.SelectionStart = 0 : article.SelectionLength = 0
        Finally
            updating = False
        End Try
    End Sub

    Protected Overrides Sub OnLayout(e As LayoutEventArgs)
        Dim compactNow = Width < Px(Me, 520)
        topicList.Width = Px(Me, 190) : listGap.Width = Px(Me, 14) : pickerGap.Height = Px(Me, 10)
        If compactNow <> isCompact Then
            isCompact = compactNow
            topicList.Visible = Not compactNow : listGap.Visible = Not compactNow
            picker.Visible = compactNow : pickerGap.Visible = compactNow
        End If
        MyBase.OnLayout(e)
    End Sub
End Class
