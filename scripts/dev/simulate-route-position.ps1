param(
    [Parameter(Mandatory = $true)][Guid]$RouteId,
    [string]$ApiBaseUrl = 'http://localhost:5212',
    [ValidateRange(1, 60)][int]$IntervalSeconds = 2,
    [ValidateRange(1, 100)][int]$StepsPerLeg = 5,
    [ValidateRange(0, 10000)][int]$Cycles = 0
)

$ErrorActionPreference = 'Stop'
$apiUri = [Uri]$ApiBaseUrl
if (-not $apiUri.IsLoopback -or $apiUri.Scheme -notin @('http', 'https')) {
    throw 'Simulatorul de dezvoltare acceptă numai un API local (localhost/127.0.0.1).'
}

$endpoint = "{0}/api/routes/{1}" -f $ApiBaseUrl.TrimEnd('/'), $RouteId
$route = Invoke-RestMethod -Method Get -Uri $endpoint
$stops = @($route.stops | Sort-Object sequence)
if ($stops.Count -lt 2) { throw 'Ruta de test trebuie să aibă cel puțin două opriri.' }
foreach ($stop in $stops) {
    if ([double]::IsNaN($stop.latitude) -or [double]::IsNaN($stop.longitude) -or
        $stop.latitude -lt -90 -or $stop.latitude -gt 90 -or
        $stop.longitude -lt -180 -or $stop.longitude -gt 180) {
        throw 'Opririle rutei au coordonate invalide.'
    }
}

Write-Host 'SIMULARE DE DEZVOLTARE — pozițiile trimise NU sunt GPS real.'
Write-Host "Ruta: $RouteId. Oprește cu Ctrl+C."
$cycle = 0
while ($Cycles -eq 0 -or $cycle -lt $Cycles) {
    for ($index = 0; $index -lt $stops.Count; $index++) {
        $start = $stops[$index]
        $end = $stops[($index + 1) % $stops.Count]
        for ($step = 0; $step -lt $StepsPerLeg; $step++) {
            $fraction = $step / $StepsPerLeg
            $body = @{
                latitude = [double]$start.latitude + ([double]$end.latitude - [double]$start.latitude) * $fraction
                longitude = [double]$start.longitude + ([double]$end.longitude - [double]$start.longitude) * $fraction
                reportedAt = [DateTimeOffset]::UtcNow.ToString('o')
                source = 'Simulated'
            } | ConvertTo-Json -Compress
            $position = Invoke-RestMethod -Method Post -Uri "$endpoint/position" -ContentType 'application/json' -Body $body
            Write-Host ("SIMULAT {0}: {1:N5}, {2:N5}" -f $position.reportedAt, $position.latitude, $position.longitude)
            Start-Sleep -Seconds $IntervalSeconds
        }
    }
    $cycle++
}
