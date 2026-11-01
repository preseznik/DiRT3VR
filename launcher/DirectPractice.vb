Imports System.Text
Imports System.Xml
Imports System.Xml.Linq

' Direct practice: DiRT 3's benchmark start (-benchmark <file>) on a chosen route and car, with the
' renderer keeping the car under the player's control (DIRT3VR_DIRECT_PRACTICE=1, ClaudeCode's
' #a1d31909; docs/research/direct-launch.md). The config file sits in the game folder under a short
' relative name and is part of the session's recovery journal, so it is removed afterwards.
Public NotInheritable Class DirectPractice
    Public Const FileName As String = "dirt3vr-direct.xml"
    ' DiRT 3 writes <prefix>_<timestamp>.xml to Documents\My Games\DiRT3\benchmarks after a finish.
    Public Const ResultPrefix As String = "DiRT3VR_direct"
    ' The player's own graphics file (in the game's /hwsave/ folder), so VR's temporary changes apply.
    Public Const HardwareSettings As String = "hardware_settings_config.xml"
    ' DiRT 3 takes up to 8 cars per benchmark track; the first is the player's (Wrench's runs 24-28).
    Public Const MaxCars As Integer = 8
    Public Const MaxOpponents As Integer = MaxCars - 1
    ' Opponent car choices: the player's class within the route's discipline, any car of that
    ' discipline, or the player's own model.
    Public Shared ReadOnly OpponentCarChoices As String() = {"class", "mixed", "same"}
    ' DiRT 3's six AI levels (shown as stars in the game); the renderer answers the game's lookup.
    Public Const MaxDifficulty As Integer = 5

    Private Sub New()
    End Sub

    ' Laps the renderer sets for the benchmark start (DIRT3VR_DIRECT_LAPS); 0 for point-to-point stages.
    Public Shared Function Laps(settings As LauncherSettings) As Integer
        Dim route = EventCatalog.Current.FindRoute(settings.DirectRoute)
        Return If(route IsNot Nothing AndAlso route.TrackType = "circuit", Math.Clamp(settings.DirectLaps, 1, 5), 0)
    End Function

    ' AI opponents for the benchmark start: circuits only for now; 0 for point-to-point stages.
    Public Shared Function Opponents(settings As LauncherSettings) As Integer
        Dim route = EventCatalog.Current.FindRoute(settings.DirectRoute)
        Return If(route IsNot Nothing AndAlso route.TrackType = "circuit", Math.Clamp(settings.DirectOpponents, 0, MaxOpponents), 0)
    End Function

    ' Which cars the opponents drive, as (car code, count) in catalog order. They are spread evenly over
    ' the chosen pool and avoid the player's model unless the pool has nothing else.
    Public Shared Function OpponentField(settings As LauncherSettings) As IReadOnlyList(Of (Code As String, Count As Integer))
        Dim count = Opponents(settings)
        Dim catalog = EventCatalog.Current
        Dim route = catalog.FindRoute(settings.DirectRoute), player = catalog.FindCar(settings.DirectCar)
        If count = 0 OrElse route Is Nothing OrElse player Is Nothing Then Return Array.Empty(Of (Code As String, Count As Integer))()
        Dim discipline = catalog.CarsFor(route.Discipline, False, "", "").ToList()
        Dim pool = If(settings.DirectOpponentCars = "same", New List(Of EventCatalog.EventCar),
                      If(settings.DirectOpponentCars = "mixed", discipline, discipline.Where(Function(car) car.VehicleClass = player.VehicleClass).ToList()))
        pool = pool.Where(Function(car) car.Code <> player.Code).ToList()
        If pool.Count = 0 Then pool.Add(player)
        Dim counts = pool.ToDictionary(Function(car) car.Code, Function(car) 0)
        For index = 0 To count - 1
            counts(pool(index * pool.Count \ count).Code) += 1
        Next
        Return pool.Where(Function(car) counts(car.Code) > 0).Select(Function(car) (car.Code, counts(car.Code))).ToArray()
    End Function

    ' The AI level the renderer reports while opponents race; -1 leaves DiRT 3's own setting.
    Public Shared Function Difficulty(settings As LauncherSettings) As Integer
        Return If(Opponents(settings) > 0, Math.Clamp(settings.DirectDifficulty, -1, MaxDifficulty), -1)
    End Function

    ' The benchmark start and the renderer's direct practice switches, for Launch and Launch VR.
    Public Shared Sub AddStart(start As ProcessStartInfo, settings As LauncherSettings)
        start.Environment("DIRT3VR_DIRECT_PRACTICE") = "1"
        Dim lapCount = Laps(settings), level = Difficulty(settings)
        If lapCount > 0 Then start.Environment("DIRT3VR_DIRECT_LAPS") = Number(lapCount)
        If level >= 0 Then start.Environment("DIRT3VR_DIRECT_DIFFICULTY") = Number(level)
        start.ArgumentList.Add("-benchmark") : start.ArgumentList.Add(FileName)
    End Sub

    Public Shared Function ConfigPath(context As InstallContext) As String
        Return Path.Combine(context.GameRoot, FileName)
    End Function

    Public Shared Function Config(settings As LauncherSettings) As Byte()
        Dim route = EventCatalog.Current.FindRoute(settings.DirectRoute)
        Dim car = EventCatalog.Current.FindCar(settings.DirectCar)
        If route Is Nothing OrElse car Is Nothing Then Throw New IOException("Choose a route and a car for direct practice.")
        Return Build(route, car.Code, OpponentField(settings))
    End Function

    ' The player's car comes first; opponents of the player's own model share its entry (number = 1 +
    ' those opponents, like the game's example_benchmark.xml), each other model gets one entry.
    Private Shared Function Build(route As EventCatalog.EventRoute, player As String, field As IEnumerable(Of (Code As String, Count As Integer))) As Byte()
        Dim track As New XElement("track", New XAttribute("country", route.Country), New XAttribute("name", route.Track), New XAttribute("route", route.Route),
                                  New XElement("car", New XAttribute("name", player), New XAttribute("number", Number(1 + field.Where(Function(entry) entry.Code = player).Sum(Function(entry) entry.Count)))))
        For Each entry In field.Where(Function(item) item.Code <> player)
            track.Add(New XElement("car", New XAttribute("name", entry.Code), New XAttribute("number", Number(entry.Count))))
        Next
        Dim document As New XDocument(New XDeclaration("1.0", "utf-8", Nothing),
            New XElement("config", New XAttribute("skipreplays", "true"), New XAttribute("outputfilename", ResultPrefix), New XAttribute("hardwaresettings", HardwareSettings), track))
        Return Encoding.UTF8.GetBytes(document.Declaration.ToString() & document.ToString(SaveOptions.DisableFormatting))
    End Function

    Private Shared Function Number(value As Integer) As String
        Return value.ToString(Globalization.CultureInfo.InvariantCulture)
    End Function

    ' A prepared config must be exactly what Config writes for some listed route and car (recovery and
    ' the elevated worker only ever write approved content).
    Public Shared Sub Validate(data As Byte())
        Dim document As XDocument
        Try
            Using stream As New MemoryStream(data), reader = XmlReader.Create(stream, New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Prohibit, .XmlResolver = Nothing, .MaxCharactersInDocument = 64 * 1024})
                document = XDocument.Load(reader)
            End Using
        Catch ex As XmlException
            Throw New IOException("Invalid direct practice configuration.", ex)
        End Try
        Dim track = document.Root?.Element("track")
        Dim cars = If(track?.Elements("car").ToArray(), Array.Empty(Of XElement)())
        Dim route = EventCatalog.Current.Routes.FirstOrDefault(Function(item) item.Country = track?.Attribute("country")?.Value AndAlso item.Track = track?.Attribute("name")?.Value AndAlso item.Route = track?.Attribute("route")?.Value)
        If route Is Nothing OrElse cars.Length = 0 OrElse cars.Any(Function(car) EventCatalog.Current.FindCar(car.Attribute("name")?.Value) Is Nothing) Then Throw New IOException("The direct practice configuration names an unknown route or car.")
        Dim counts = cars.Select(Function(car) ParseCount(car.Attribute("number")?.Value)).ToArray()
        Dim player = cars(0).Attribute("name").Value
        Dim field = cars.Skip(1).Select(Function(car, index) (car.Attribute("name").Value, counts(index + 1))).Prepend((player, counts(0) - 1)).Where(Function(entry) entry.Item2 > 0).
                        Select(Function(entry) (Code:=entry.Item1, Count:=entry.Item2)).ToArray()
        If counts.Any(Function(value) value < 1) OrElse counts.Sum() > MaxCars OrElse (counts.Sum() > 1 AndAlso route.TrackType <> "circuit") OrElse
           cars.Skip(1).Any(Function(car) car.Attribute("name").Value = player) Then Throw New IOException("The direct practice configuration has an unsupported field of cars.")
        Dim expected = Build(route, player, field)
        If Not expected.SequenceEqual(data) Then Throw New IOException("Prepared session data does not match its approved transformation: direct practice configuration.")
    End Sub

    Private Shared Function ParseCount(text As String) As Integer
        Dim value As Integer
        Return If(Integer.TryParse(text, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, value), value, 0)
    End Function

    ' Removes DiRT 3's benchmark result files for direct practice (fps samples only, no player data).
    Public Shared Sub RemoveResults(context As InstallContext)
        Try
            Dim folder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(context.GraphicsPath)), "benchmarks")
            If Not Directory.Exists(folder) Then Return
            SafeFiles.NoLinks(folder)
            For Each file In Directory.EnumerateFiles(folder, ResultPrefix & "_*.xml").Take(100).ToArray()
                SafeFiles.NoLinks(file)
                IO.File.Delete(file)
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Only a small results file is left behind.
        End Try
    End Sub
End Class
