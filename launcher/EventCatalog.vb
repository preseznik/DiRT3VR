Imports System.Text.Json

' DiRT 3's disciplines, routes and cars for direct practice, from tools/data/event-catalog.json
' (ClaudeCode's extract of the game's own database, #5b02d01b). The launcher embeds that file unchanged.
' Names are the game's own, in its uppercase spelling.
Public NotInheritable Class EventCatalog
    Public Class EventDiscipline
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property TrackType As String = ""
        Public Property StaggeredStart As Boolean
        Public Property Laps As New List(Of Integer)

        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    ' One of a route's weather/lighting pairs (#0e2b82ba): DiRT 3's own track_model_conditions row, with
    ' its Single Race name. IsDefault marks the row a direct start takes by itself.
    Public Class EventCondition
        Public Property Id As Integer
        Public Property Name As String = ""
        Public Property Weather As String = ""
        Public Property Lighting As String = ""
        Public Property IsDefault As Boolean

        Public Overrides Function ToString() As String
            Dim text = If(Name = "", Weather & " / " & Lighting, Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Name.ToLowerInvariant()))
            Return If(IsDefault, text & " (default)", text)
        End Function
    End Class

    Public Class EventRoute
        Public Property Id As Integer
        Public Property Discipline As String = ""
        Public Property Country As String = ""
        Public Property Track As String = ""
        Public Property Route As String = ""
        Public Property Name As String = ""
        Public Property TrackName As String = ""
        Public Property LocationName As String = ""
        Public Property TrackType As String = ""
        Public Property LengthMetres As Integer
        ' Single Race order; empty for a catalog without conditions (the route's default only).
        Public Property Conditions As New List(Of EventCondition)

        Public Overrides Function ToString() As String
            Dim length = If(LengthMetres > 0, " (" & (LengthMetres / 1000.0).ToString("0.0", Globalization.CultureInfo.InvariantCulture) & " km)", "")
            Return LocationName & " – " & Name & length
        End Function
    End Class

    Public Class EventCar
        Public Property Code As String = ""
        Public Property Name As String = ""
        Public Property Discipline As String = ""
        Public Property VehicleClass As String = ""
        Public Property VehicleClassName As String = ""

        Public Overrides Function ToString() As String
            Return Name & " (" & VehicleClassName & ")"
        End Function
    End Class

    Public Property Schema As Integer
    Public Property Disciplines As New List(Of EventDiscipline)
    Public Property Routes As New List(Of EventRoute)
    Public Property Cars As New List(Of EventCar)

    Private Shared ReadOnly Loaded As New Lazy(Of EventCatalog)(AddressOf LoadEmbedded)

    Public Shared ReadOnly Property Current As EventCatalog
        Get
            Return Loaded.Value
        End Get
    End Property

    Private Shared Function LoadEmbedded() As EventCatalog
        Using stream = GetType(EventCatalog).Assembly.GetManifestResourceStream("DiRT3VR.event-catalog.json")
            If stream Is Nothing Then Throw New IOException("The event catalog is missing from this launcher build.")
            Using reader As New StreamReader(stream)
                Return Parse(reader.ReadToEnd())
            End Using
        End Using
    End Function

    Private Shared ReadOnly Identifier As New Text.RegularExpressions.Regex("\A[a-z0-9_]{1,32}\z")

    Public Shared Function Parse(json As String) As EventCatalog
        Dim catalog = JsonSerializer.Deserialize(Of EventCatalog)(json, New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})
        If catalog Is Nothing OrElse catalog.Schema <> 1 OrElse catalog.Disciplines.Count = 0 OrElse catalog.Routes.Count = 0 OrElse catalog.Cars.Count = 0 Then Throw New IOException("Unsupported event catalog.")
        Dim disciplineIds = catalog.Disciplines.Select(Function(item) item.Id).ToHashSet()
        ' Values go into the benchmark file DiRT 3 reads, so only plain identifiers are accepted.
        If catalog.Disciplines.Any(Function(item) Not Identifier.IsMatch(item.Id)) OrElse disciplineIds.Count <> catalog.Disciplines.Count Then Throw New IOException("Invalid event catalog disciplines.")
        If catalog.Routes.Any(Function(item) Not disciplineIds.Contains(item.Discipline) OrElse Not Identifier.IsMatch(item.Country) OrElse Not Identifier.IsMatch(item.Track) OrElse Not Identifier.IsMatch(item.Route)) OrElse
           catalog.Routes.Select(Function(item) item.Id).Distinct().Count() <> catalog.Routes.Count Then Throw New IOException("Invalid event catalog routes.")
        ' Conditions become the renderer's weather and lighting values, so only names it knows are accepted.
        If catalog.Routes.Any(Function(item) item.Conditions Is Nothing OrElse (item.Conditions.Count > 0 AndAlso
                                  (item.Conditions.Where(Function(condition) condition.IsDefault).Count() <> 1 OrElse
                                   item.Conditions.Select(Function(condition) condition.Id).Distinct().Count() <> item.Conditions.Count OrElse
                                   item.Conditions.Any(Function(condition) Not DirectPractice.WeatherValues.ContainsKey(If(condition.Weather, "")) OrElse Not DirectPractice.LightingValues.ContainsKey(If(condition.Lighting, "")))))) Then
            Throw New IOException("Invalid event catalog conditions.")
        End If
        If catalog.Cars.Any(Function(item) Not disciplineIds.Contains(item.Discipline) OrElse Not Identifier.IsMatch(item.Code)) OrElse
           catalog.Cars.Select(Function(item) item.Code).Distinct().Count() <> catalog.Cars.Count Then Throw New IOException("Invalid event catalog cars.")
        Return catalog
    End Function

    Public Function FindRoute(id As Integer) As EventRoute
        Return Routes.FirstOrDefault(Function(item) item.Id = id)
    End Function

    Public Function FindCar(code As String) As EventCar
        Return Cars.FirstOrDefault(Function(item) item.Code = code)
    End Function

    Public Function FindDiscipline(id As String) As EventDiscipline
        Return Disciplines.FirstOrDefault(Function(item) item.Id = id)
    End Function

    ' Routes of one discipline, by location and name.
    Public Function RoutesFor(discipline As String) As IEnumerable(Of EventRoute)
        Return Routes.Where(Function(item) item.Discipline = discipline).OrderBy(Function(item) item.LocationName, StringComparer.Ordinal).ThenBy(Function(item) item.Name, StringComparer.Ordinal)
    End Function

    ' Cars for the picker: the route's discipline unless all are requested, then a class and a search
    ' over name, code and class.
    Public Function CarsFor(discipline As String, allDisciplines As Boolean, vehicleClass As String, search As String) As IEnumerable(Of EventCar)
        Dim words = If(search, "").Split(" "c, StringSplitOptions.RemoveEmptyEntries)
        Return Cars.Where(Function(item) (allDisciplines OrElse item.Discipline = discipline) AndAlso
                                          (String.IsNullOrEmpty(vehicleClass) OrElse item.VehicleClassName = vehicleClass) AndAlso
                                          words.All(Function(word) item.Name.Contains(word, StringComparison.OrdinalIgnoreCase) OrElse item.Code.Contains(word, StringComparison.OrdinalIgnoreCase) OrElse item.VehicleClassName.Contains(word, StringComparison.OrdinalIgnoreCase))).
                    OrderBy(Function(item) item.VehicleClassName, StringComparer.Ordinal).ThenBy(Function(item) item.Name, StringComparer.Ordinal)
    End Function
End Class
