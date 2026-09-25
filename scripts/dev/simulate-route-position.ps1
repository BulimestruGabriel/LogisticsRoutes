param(
    [Parameter(Mandatory = $true)][Guid]$RouteId,
    [string]$ApiBaseUrl = 'http://localhost:5212',
    [string]$OsrmBaseUrl = 'http://127.0.0.1:5000',
    [ValidateRange(1, 60)][int]$IntervalSeconds = 2,
    [ValidateRange(1, 100)][int]$StepsPerLeg = 5,
    [ValidateRange(0, 10000)][int]$Cycles = 0
)

$ErrorActionPreference = 'Stop'
foreach ($baseUrl in @($ApiBaseUrl, $OsrmBaseUrl)) {
    $uri = $null
    if (-not [Uri]::TryCreate($baseUrl, [UriKind]::Absolute, [ref]$uri) -or
        -not $uri.IsLoopback -or $uri.Scheme -notin @('http', 'https')) {
        throw 'Simulatorul de dezvoltare acceptă numai API și OSRM locale (localhost/127.0.0.1).'
    }
}

function Get-DistanceMeters([double]$latitude1, [double]$longitude1,
    [double]$latitude2, [double]$longitude2) {
    $radians = [Math]::PI / 180
    $latitudeDelta = ($latitude2 - $latitude1) * $radians
    $longitudeDelta = ($longitude2 - $longitude1) * $radians
    $haversine = [Math]::Pow([Math]::Sin($latitudeDelta / 2), 2) +
        [Math]::Cos($latitude1 * $radians) * [Math]::Cos($latitude2 * $radians) *
        [Math]::Pow([Math]::Sin($longitudeDelta / 2), 2)
    return 2 * 6371000 * [Math]::Asin([Math]::Min(1.0, [Math]::Sqrt($haversine)))
}

$endpoint = "{0}/api/routes/{1}" -f $ApiBaseUrl.TrimEnd('/'), $RouteId
$route = Invoke-RestMethod -Method Get -Uri $endpoint
$stops = @($route.stops | Sort-Object sequence)
if ($stops.Count -lt 2) { throw 'Ruta de test trebuie să aibă cel puțin două opriri.' }
foreach ($stop in $stops) {
    $latitude = [double]$stop.latitude
    $longitude = [double]$stop.longitude
    if ([double]::IsNaN($latitude) -or [double]::IsInfinity($latitude) -or
        [double]::IsNaN($longitude) -or [double]::IsInfinity($longitude) -or
        $latitude -lt -90 -or $latitude -gt 90 -or
        $longitude -lt -180 -or $longitude -gt 180) {
        throw 'Opririle rutei au coordonate invalide.'
    }
}

$culture = [Globalization.CultureInfo]::InvariantCulture
$coordinates = ($stops | ForEach-Object {
    ([double]$_.longitude).ToString('R', $culture) + ',' +
        ([double]$_.latitude).ToString('R', $culture)
}) -join ';'
$osrmUri = '{0}/route/v1/driving/{1}?overview=full&geometries=geojson&steps=false' -f
    $OsrmBaseUrl.TrimEnd('/'), $coordinates

$roadPoints = New-Object 'System.Collections.Generic.List[object]'
$cumulativeMeters = New-Object 'System.Collections.Generic.List[double]'
$useRoad = $false
try {
    $response = Invoke-RestMethod -Method Get -Uri $osrmUri -TimeoutSec 10
    if ($response.code -ne 'Ok' -or -not $response.routes -or
        $response.routes[0].geometry.type -ne 'LineString' -or
        -not $response.routes[0].geometry.coordinates) {
        throw "OSRM a răspuns '$($response.code)': $($response.message)"
    }
    foreach ($coordinate in $response.routes[0].geometry.coordinates) {
        if ($null -eq $coordinate -or $coordinate.Count -lt 2) {
            throw 'Geometria OSRM conține un punct incomplet.'
        }
        $longitude = [double]$coordinate[0]
        $latitude = [double]$coordinate[1]
        if ([double]::IsNaN($latitude) -or [double]::IsInfinity($latitude) -or
            [double]::IsNaN($longitude) -or [double]::IsInfinity($longitude) -or
            $latitude -lt -90 -or $latitude -gt 90 -or
            $longitude -lt -180 -or $longitude -gt 180) {
            throw 'Geometria OSRM conține coordonate invalide.'
        }
        $point = [pscustomobject]@{ latitude = $latitude; longitude = $longitude }
        if ($roadPoints.Count -eq 0) {
            $cumulativeMeters.Add(0)
        } else {
            $previous = $roadPoints[$roadPoints.Count - 1]
            $distance = Get-DistanceMeters $previous.latitude $previous.longitude $latitude $longitude
            $cumulativeMeters.Add($cumulativeMeters[$cumulativeMeters.Count - 1] + $distance)
        }
        $roadPoints.Add($point)
    }
    if ($roadPoints.Count -lt 2 -or $cumulativeMeters[$cumulativeMeters.Count - 1] -le 0) {
        throw 'OSRM nu a întors o geometrie rutieră utilizabilă.'
    }
    # Returul folosește aceeași linie rutieră, astfel încât marcajul rămâne pe linia din hartă.
    for ($index = $roadPoints.Count - 2; $index -ge 0; $index--) {
        $previous = $roadPoints[$roadPoints.Count - 1]
        $point = $roadPoints[$index]
        $distance = Get-DistanceMeters $previous.latitude $previous.longitude $point.latitude $point.longitude
        $cumulativeMeters.Add($cumulativeMeters[$cumulativeMeters.Count - 1] + $distance)
        $roadPoints.Add($point)
    }
    $useRoad = $true
} catch {
    $reason = $_.Exception.Message
    $errorBody = $_.ErrorDetails.Message
    if (-not $errorBody -and $_.Exception.Response) {
        try {
            $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            $errorBody = $reader.ReadToEnd()
            $reader.Dispose()
        } catch { }
    }
    if ($errorBody) {
        try {
            $osrmError = $errorBody | ConvertFrom-Json
            if ($osrmError.code) {
                $reason = "OSRM $($osrmError.code): $($osrmError.message)"
            }
        } catch { }
    }
    Write-Warning "FALLBACK LINIE DREAPTĂ: OSRM local nu a furnizat traseul. Motiv: $reason"
}

Write-Host 'SIMULARE DE DEZVOLTARE — pozițiile trimise NU sunt GPS real.'
Write-Host "Ruta: $RouteId. Oprește cu Ctrl+C."
if ($useRoad) {
    Write-Host ("Traseu OSRM local: {0} puncte, {1:N1} km; pași egali ca distanță pe geometria rutieră." -f
        $roadPoints.Count, ($cumulativeMeters[$cumulativeMeters.Count - 1] / 1000))
} else {
    Write-Host 'FALLBACK LINIE DREAPTĂ: interpolare între opriri.'
}

$cycle = 0
while ($Cycles -eq 0 -or $cycle -lt $Cycles) {
    $samplesPerCycle = $StepsPerLeg * $stops.Count
    $segmentIndex = 0
    for ($sample = 0; $sample -lt $samplesPerCycle; $sample++) {
        if ($useRoad) {
            $targetMeters = $cumulativeMeters[$cumulativeMeters.Count - 1] * $sample / $samplesPerCycle
            while ($segmentIndex -lt $roadPoints.Count - 2 -and
                $cumulativeMeters[$segmentIndex + 1] -lt $targetMeters) {
                $segmentIndex++
            }
            $segmentMeters = $cumulativeMeters[$segmentIndex + 1] - $cumulativeMeters[$segmentIndex]
            $fraction = if ($segmentMeters -gt 0) {
                ($targetMeters - $cumulativeMeters[$segmentIndex]) / $segmentMeters
            } else { 0 }
            $start = $roadPoints[$segmentIndex]
            $end = $roadPoints[$segmentIndex + 1]
        } else {
            $leg = [Math]::Floor($sample / $StepsPerLeg)
            $start = $stops[$leg]
            $end = $stops[($leg + 1) % $stops.Count]
            $fraction = ($sample % $StepsPerLeg) / $StepsPerLeg
        }
        $body = @{
            latitude = [double]$start.latitude + ([double]$end.latitude - [double]$start.latitude) * $fraction
            longitude = [double]$start.longitude + ([double]$end.longitude - [double]$start.longitude) * $fraction
            reportedAt = [DateTimeOffset]::UtcNow.ToString('o')
            source = 'Simulated'
        } | ConvertTo-Json -Compress

        $position = Invoke-RestMethod -Method Post -Uri "$endpoint/position" -ContentType 'application/json' -Body $body
        Write-Host ("SIMULAT {0}: {1:N5}, {2:N5}" -f $position.reportedAt, $position.latitude, $position.longitude)
        if ($sample -lt $samplesPerCycle - 1 -or $Cycles -eq 0 -or $cycle -lt $Cycles - 1) {
            Start-Sleep -Seconds $IntervalSeconds
        }
    }
    $cycle++
}
