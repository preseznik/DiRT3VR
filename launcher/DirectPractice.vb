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

    Private Sub New()
    End Sub

    ' Laps the renderer sets for the benchmark start (DIRT3VR_DIRECT_LAPS); 0 for point-to-point stages.
    Public Shared Function Laps(settings As LauncherSettings) As Integer
        Dim route = EventCatalog.Current.FindRoute(settings.DirectRoute)
        Return If(route IsNot Nothing AndAlso route.TrackType = "circuit", Math.Clamp(settings.DirectLaps, 1, 5), 0)
    End Function

    Public Shared Function ConfigPath(context As InstallContext) As String
        Return Path.Combine(context.GameRoot, FileName)
    End Function

    Public Shared Function Config(settings As LauncherSettings) As Byte()
        Dim route = EventCatalog.Current.FindRoute(settings.DirectRoute)
        Dim car = EventCatalog.Current.FindCar(settings.DirectCar)
        If route Is Nothing OrElse car Is Nothing Then Throw New IOException("Choose a route and a car for direct practice.")
        Dim document As New XDocument(New XDeclaration("1.0", "utf-8", Nothing),
            New XElement("config", New XAttribute("skipreplays", "true"), New XAttribute("outputfilename", ResultPrefix), New XAttribute("hardwaresettings", HardwareSettings),
                New XElement("track", New XAttribute("country", route.Country), New XAttribute("name", route.Track), New XAttribute("route", route.Route),
                    New XElement("car", New XAttribute("name", car.Code), New XAttribute("number", "1")))))
        Return Encoding.UTF8.GetBytes(document.Declaration.ToString() & document.ToString(SaveOptions.DisableFormatting))
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
        Dim car = track?.Element("car")
        Dim route = EventCatalog.Current.Routes.FirstOrDefault(Function(item) item.Country = track?.Attribute("country")?.Value AndAlso item.Track = track?.Attribute("name")?.Value AndAlso item.Route = track?.Attribute("route")?.Value)
        Dim code = car?.Attribute("name")?.Value
        If route Is Nothing OrElse EventCatalog.Current.FindCar(code) Is Nothing Then Throw New IOException("The direct practice configuration names an unknown route or car.")
        Dim expected = Config(New LauncherSettings With {.DirectRoute = route.Id, .DirectCar = code})
        If Not expected.SequenceEqual(data) Then Throw New IOException("Prepared session data does not match its approved transformation: direct practice configuration.")
    End Sub

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
