param([int[]]$Ids = @(49791, 45971, 19920))

$uri = [Uri]"ws://78.116.140.30:3100"
$ws  = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).Wait()

function Recv($timeout = 10000) {
    $buf = New-Object byte[] 262144
    $sb  = New-Object System.Text.StringBuilder
    do {
        $seg = New-Object System.ArraySegment[byte] @(,$buf)
        $cts = New-Object System.Threading.CancellationTokenSource
        $cts.CancelAfter($timeout)
        $r = $ws.ReceiveAsync($seg, $cts.Token).Result
        $sb.Append([System.Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)) | Out-Null
    } while (-not $r.EndOfMessage)
    $sb.ToString()
}

function Send($obj) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Compress))
    $seg   = New-Object System.ArraySegment[byte] @(,$bytes)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).Wait()
}

# Drain hello
$h = Recv; Write-Host "=== hello ===" ; Write-Host $h

# Drain snapshot (may be large)
$snap = Recv 12000
$parsed = $snap | ConvertFrom-Json
Write-Host "`n=== snapshot type: $($parsed.type) ==="

# Query each item
foreach ($id in $Ids) {
    Send @{ type = "ACQ_QUERY"; data = @{ itemId = $id } }
    $raw = Recv 10000
    Write-Host "`n=== ACQ_RESULT for itemId $id ==="
    $raw | ConvertFrom-Json | ConvertTo-Json -Depth 20 | Write-Host
}

$ws.CloseAsync("NormalClosure", "done", [System.Threading.CancellationToken]::None).Wait()
