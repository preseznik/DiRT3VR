Imports System.Text.Json
Imports System.Xml.Linq

' DiRT 3's graphics quality options and presets (DirectX 11 values), from tools/data/graphics-options.json,
' which tools/extract_graphics_options.py generates from the game's own hardware_settings_options.xml.
' The launcher embeds that file unchanged. A choice writes exactly the attributes the game's own option
' writes; everything else in the graphics file is left alone. Adapted from GRID2VR's graphics catalog.
Public NotInheritable Class GraphicsCatalog
    Public Class Choice
        Public Property Id As String = ""
        Public Property Label As String = ""
        Public Property Attributes As New Dictionary(Of String, String)
    End Class

    Public Class Category
        Public Property Field As String = ""
        Public Property GameCategory As String = ""
        Public Property Label As String = ""
        Public Property [Default] As String = ""
        Public Property Choices As New List(Of Choice)

        Public Function Find(id As String) As Choice
            Return Choices.FirstOrDefault(Function(choice) choice.Id = id)
        End Function
    End Class

    Public Class Preset
        Public Property Id As String = ""
        Public Property Label As String = ""
        Public Property Values As New Dictionary(Of String, String)
    End Class

    Public Property Schema As Integer
    Public Property Categories As New List(Of Category)
    Public Property Presets As New List(Of Preset)

    Private Shared ReadOnly Loaded As New Lazy(Of GraphicsCatalog)(AddressOf LoadEmbedded)

    ' The catalog shipped inside the launcher.
    Public Shared ReadOnly Property Current As GraphicsCatalog
        Get
            Return Loaded.Value
        End Get
    End Property

    Private Shared Function LoadEmbedded() As GraphicsCatalog
        Using stream = GetType(GraphicsCatalog).Assembly.GetManifestResourceStream("DiRT3VR.graphics-options.json")
            If stream Is Nothing Then Throw New IOException("The graphics options catalog is missing from this launcher build.")
            Using reader As New StreamReader(stream)
                Return Parse(reader.ReadToEnd())
            End Using
        End Using
    End Function

    Public Shared Function Parse(json As String) As GraphicsCatalog
        Dim catalog = JsonSerializer.Deserialize(Of GraphicsCatalog)(json, New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})
        If catalog Is Nothing OrElse catalog.Schema <> 1 OrElse catalog.Categories.Count = 0 Then Throw New IOException("Unsupported graphics options catalog.")
        For Each category In catalog.Categories
            If category.Field = "" OrElse category.Choices.Count = 0 OrElse category.Find(category.Default) Is Nothing Then Throw New IOException("Invalid graphics category: " & category.Field)
            For Each choice In category.Choices
                For Each key In choice.Attributes.Keys
                    Dim parts = key.Split("@"c)
                    If parts.Length <> 2 OrElse parts(0) = "" OrElse parts(1) = "" OrElse parts(0).Contains("/"c) Then Throw New IOException("Invalid graphics attribute: " & key)
                Next
            Next
        Next
        For Each preset In catalog.Presets
            For Each value In preset.Values
                Dim category = catalog.Categories.FirstOrDefault(Function(item) item.Field = value.Key)
                If category Is Nothing OrElse category.Find(value.Value) Is Nothing Then Throw New IOException("Invalid graphics preset: " & preset.Id)
            Next
        Next
        Return catalog
    End Function

    ' Choices in catalog order, without "Use game setting" entries, so equal choices serialize equally.
    Public Function Normalize(choices As IDictionary(Of String, String)) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)
        If choices Is Nothing Then Return result
        For Each pair In choices
            Dim category = Categories.FirstOrDefault(Function(item) item.Field = pair.Key)
            If category Is Nothing Then Throw New IOException("Unknown graphics option: " & pair.Key)
            If pair.Value <> "game" AndAlso category.Find(pair.Value) Is Nothing Then Throw New IOException("Choose a listed value for " & category.Label & ".")
        Next
        For Each category In Categories
            Dim value As String = Nothing
            If choices.TryGetValue(category.Field, value) AndAlso value <> "game" Then result.Add(category.Field, value)
        Next
        Return result
    End Function

    ' The preset whose values these are exactly; Nothing for "Use game settings" (no choices) or Custom.
    Public Function MatchingPreset(choices As IDictionary(Of String, String)) As Preset
        Dim normalized = Normalize(choices)
        If normalized.Count = 0 Then Return Nothing
        Return Presets.FirstOrDefault(Function(preset) preset.Values.Count = normalized.Count AndAlso preset.Values.All(Function(value) normalized.ContainsKey(value.Key) AndAlso normalized(value.Key) = value.Value))
    End Function

    ' Writes the chosen values into the game's settings document. Every element named by a choice must
    ' exist exactly once; a missing attribute on it is added (the game writes them all).
    Public Function Apply(root As XElement, choices As IDictionary(Of String, String)) As Boolean
        Dim changed = False
        For Each pair In Normalize(choices)
            Dim choice = Categories.First(Function(item) item.Field = pair.Key).Find(pair.Value)
            For Each written In choice.Attributes
                Dim parts = written.Key.Split("@"c)
                Dim found = root.Elements(parts(0)).Take(2).ToArray()
                If found.Length <> 1 Then Throw New IOException("Expected one '" & parts(0) & "' entry in the game graphics file; it was preserved.")
                If found(0).Attribute(parts(1))?.Value <> written.Value Then
                    found(0).SetAttributeValue(parts(1), written.Value)
                    changed = True
                End If
            Next
        Next
        Return changed
    End Function
End Class

' One set of quality controls (preset picker plus a dropdown per option) for desktop or VR. The first
' entry of each dropdown leaves the option alone: "Use game setting" for desktop, "Same as desktop" for VR.
Public NotInheritable Class QualityChoices
    Private ReadOnly preset As System.Windows.Forms.ComboBox
    Private ReadOnly boxes As New Dictionary(Of String, System.Windows.Forms.ComboBox)
    Private updating As Boolean
    Public Event Changed()

    Public Sub New(parent As System.Windows.Forms.Control, namePrefix As String, unsetOption As String, unsetPreset As String)
        Dim catalog = GraphicsCatalog.Current
        preset = New System.Windows.Forms.ComboBox With {.Name = namePrefix & "Preset", .DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList, .AccessibleName = namePrefix & " preset"}
        preset.Items.Add(unsetPreset)
        For Each item In catalog.Presets
            preset.Items.Add(If(item.Id = "medium", item.Label & " (game default)", item.Label))
        Next
        preset.Items.Add("Custom")
        StyleChoice(preset, parent)
        Field(parent, "Preset", preset)
        For Each category In catalog.Categories
            Dim box As New System.Windows.Forms.ComboBox With {.Name = namePrefix & category.Field, .DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList, .AccessibleName = category.Label}
            box.Items.Add(unsetOption)
            For Each choice In category.Choices
                box.Items.Add(If(choice.Id = category.Default, choice.Label & " (game default)", choice.Label))
            Next
            StyleChoice(box, parent)
            Field(parent, category.Label, box)
            boxes.Add(category.Field, box)
            AddHandler box.SelectedIndexChanged, Sub()
                                                     If updating Then Return
                                                     ShowPreset()
                                                     RaiseEvent Changed()
                                                 End Sub
        Next
        AddHandler preset.SelectedIndexChanged, Sub() PresetChosen()
        Values = Nothing
    End Sub

    ' The chosen options in catalog order; options left on the first entry are absent.
    Public Property Values As Dictionary(Of String, String)
        Get
            Dim result As New Dictionary(Of String, String)
            For Each category In GraphicsCatalog.Current.Categories
                Dim index = boxes(category.Field).SelectedIndex
                If index > 0 Then result.Add(category.Field, category.Choices(index - 1).Id)
            Next
            Return result
        End Get
        Set(value As Dictionary(Of String, String))
            updating = True
            Try
                For Each category In GraphicsCatalog.Current.Categories
                    Dim chosen As String = Nothing
                    Dim index = If(value IsNot Nothing AndAlso value.TryGetValue(category.Field, chosen), category.Choices.FindIndex(Function(item) item.Id = chosen), -1)
                    boxes(category.Field).SelectedIndex = index + 1
                Next
            Finally
                updating = False
            End Try
            ShowPreset()
        End Set
    End Property

    Private Sub PresetChosen()
        If updating Then Return
        Dim catalog = GraphicsCatalog.Current
        Dim index = preset.SelectedIndex
        If index = 0 Then
            Values = New Dictionary(Of String, String)
        ElseIf index >= 1 AndAlso index <= catalog.Presets.Count Then
            Values = New Dictionary(Of String, String)(catalog.Presets(index - 1).Values)
        Else
            Return ' Custom keeps the individual choices.
        End If
        RaiseEvent Changed()
    End Sub

    ' The picker names the preset the options match, or Custom.
    Private Sub ShowPreset()
        Dim catalog = GraphicsCatalog.Current
        Dim current = Values
        Dim match = catalog.MatchingPreset(current)
        updating = True
        Try
            preset.SelectedIndex = If(current.Count = 0, 0, If(match Is Nothing, catalog.Presets.Count + 1, catalog.Presets.IndexOf(match) + 1))
        Finally
            updating = False
        End Try
    End Sub
End Class
