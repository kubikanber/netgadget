#Requires -Version 5.1
<#
    Ağ Etkinliği — masaüstü gadget'ı
    İnternet üzerinden veri alışverişi yapan uygulamaları (TCP + UDP) canlı olarak gösterir.

    -NoElevate   Yönetici izni istemeden (sınırlı modda) başlat.
    -Snapshot    Pencereyi ekran dışında açıp verilen yola PNG kaydeder ve çıkar (test için).
#>
param(
    [switch]$NoElevate,
    [string]$Snapshot
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

# Uygulama başına trafik için yönetici izni gerekir; reddedilirse sınırlı modda devam edilir.
if (-not $isAdmin -and -not $NoElevate -and -not $Snapshot) {
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-WindowStyle', 'Hidden',
            '-File', ('"{0}"' -f $PSCommandPath), '-NoElevate')
        exit
    } catch { }
}

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\NetGadget_AgEtkinligi', [ref]$createdNew)
if (-not $createdNew -and -not $Snapshot) { exit }

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'NetSampler.cs'), (Join-Path $PSScriptRoot 'Inspector.cs') `
    -ReferencedAssemblies System.Management, ([psobject].Assembly.Location)

# ---------- Ayarlar ----------
$settingsDir = Join-Path $env:APPDATA 'NetGadget'
$settingsPath = Join-Path $settingsDir 'settings.json'
$settings = [pscustomobject]@{
    Left = $null; Top = $null; Topmost = $true; ActiveOnly = $false; MaxRows = 10
    ReverseDns = $true; LocalNames = $false; SortBy = 'Rate'
}
if (-not $Snapshot -and (Test-Path $settingsPath)) {
    try {
        $loaded = Get-Content -Raw -Path $settingsPath | ConvertFrom-Json
        foreach ($p in $loaded.PSObject.Properties) {
            if ($settings.PSObject.Properties[$p.Name]) { $settings.($p.Name) = $p.Value }
        }
    } catch { }
}

$SortModes = [ordered]@{
    Rate        = @{ Label = 'Anlık hız'; Head = 'anlık hıza göre' }
    Session     = @{ Label = 'Oturum toplamı'; Head = 'oturum toplamına göre' }
    Connections = @{ Label = 'Bağlantı sayısı'; Head = 'bağlantı sayısına göre' }
    Name        = @{ Label = 'Ad (A–Z)'; Head = 'ada göre' }
    Risk        = @{ Label = 'Şüphe puanı'; Head = 'şüphe puanına göre' }
}
if (-not $SortModes.Contains([string]$settings.SortBy)) { $settings.SortBy = 'Rate' }

function Save-Settings {
    if ($Snapshot) { return }
    try {
        if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir | Out-Null }
        $settings | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8
    } catch { }
}

# ---------- Pencere ----------
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Ağ Etkinliği" Width="408" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ShowInTaskbar="False" ResizeMode="NoResize" FontFamily="Segoe UI"
        TextOptions.TextFormattingMode="Display" UseLayoutRounding="True">
  <Window.Resources>
    <Style x:Key="ThinThumb" TargetType="Thumb">
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Thumb">
            <Border x:Name="T" CornerRadius="3" Background="#3DFFFFFF"/>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="T" Property="Background" Value="#66FFFFFF"/></Trigger>
              <Trigger Property="IsDragging" Value="True"><Setter TargetName="T" Property="Background" Value="#8CFFFFFF"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType="ScrollBar">
      <Setter Property="Width" Value="6"/>
      <Setter Property="MinWidth" Value="6"/>
      <Setter Property="Margin" Value="4,0,0,0"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="ScrollBar">
            <Border Background="#0DFFFFFF" CornerRadius="3">
              <Track x:Name="PART_Track" IsDirectionReversed="True">
                <Track.Thumb><Thumb Style="{StaticResource ThinThumb}" MinHeight="24"/></Track.Thumb>
              </Track>
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>
  <Border x:Name="Root" CornerRadius="12" Background="#EB15191F" BorderBrush="#2EFFFFFF"
          BorderThickness="1" Padding="14,12,14,10" Margin="4">
    <StackPanel>
      <Grid>
        <StackPanel Orientation="Horizontal">
          <Ellipse x:Name="LiveDot" Width="7" Height="7" Fill="#FF4CD07D" VerticalAlignment="Center" Margin="0,1,7,0"/>
          <TextBlock Text="Ağ Etkinliği" Foreground="#FFF2F4F7" FontSize="14" FontWeight="SemiBold"/>
        </StackPanel>
        <TextBlock x:Name="ModeText" HorizontalAlignment="Right" VerticalAlignment="Center"
                   FontSize="10.5" Foreground="#FF8B95A1"/>
      </Grid>

      <Grid Margin="0,10,0,0">
        <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
        <StackPanel>
          <TextBlock Text="↓ İNDİRME" FontSize="9.5" FontWeight="SemiBold" Foreground="#FF8B95A1"/>
          <TextBlock x:Name="TotalIn" Text="—" FontSize="19" FontWeight="SemiBold" Foreground="#FF4FC3F7"/>
        </StackPanel>
        <StackPanel Grid.Column="1">
          <TextBlock Text="↑ YÜKLEME" FontSize="9.5" FontWeight="SemiBold" Foreground="#FF8B95A1"/>
          <TextBlock x:Name="TotalOut" Text="—" FontSize="19" FontWeight="SemiBold" Foreground="#FFFFB74D"/>
        </StackPanel>
      </Grid>

      <Border Margin="0,8,0,0" Height="38" CornerRadius="6" Background="#0FFFFFFF">
        <Canvas x:Name="Graph" ClipToBounds="True" Margin="2">
          <Polyline x:Name="LineOut" Stroke="#FFFFB74D" StrokeThickness="1.4" StrokeLineJoin="Round"/>
          <Polyline x:Name="LineIn" Stroke="#FF4FC3F7" StrokeThickness="1.4" StrokeLineJoin="Round"/>
        </Canvas>
      </Border>

      <Grid Margin="0,12,0,4">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="*"/><ColumnDefinition Width="68"/><ColumnDefinition Width="68"/>
        </Grid.ColumnDefinitions>
        <TextBlock x:Name="HeadApp" Text="UYGULAMA" FontSize="9.5" FontWeight="SemiBold" Foreground="#FF6B7480"/>
        <TextBlock x:Name="HeadIn" Grid.Column="1" Text="↓" FontSize="9.5" FontWeight="SemiBold" Foreground="#FF6B7480" HorizontalAlignment="Right" Margin="0,0,6,0"/>
        <TextBlock x:Name="HeadOut" Grid.Column="2" Text="↑" FontSize="9.5" FontWeight="SemiBold" Foreground="#FF6B7480" HorizontalAlignment="Right" Margin="0,0,6,0"/>
      </Grid>

      <ScrollViewer x:Name="Scroller" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
        <StackPanel x:Name="List"/>
      </ScrollViewer>
      <TextBlock x:Name="Footer" FontSize="10" Foreground="#FF6B7480" Margin="0,8,0,0" TextWrapping="Wrap"/>
    </StackPanel>
  </Border>
</Window>
'@

$window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in 'Root', 'LiveDot', 'ModeText', 'TotalIn', 'TotalOut', 'Graph', 'LineIn', 'LineOut', 'HeadApp', 'HeadIn', 'HeadOut',
    'Scroller', 'List', 'Footer') {
    $ui[$n] = $window.FindName($n)
}
$ui.Scroller.MaxHeight = [Math]::Max(240, [System.Windows.SystemParameters]::WorkArea.Height - 290)

$brushConv = New-Object Windows.Media.BrushConverter
function Get-Brush([string]$hex) { $b = $brushConv.ConvertFromString($hex); $b.Freeze(); $b }
$brText = Get-Brush '#FFF2F4F7'
$brSub = Get-Brush '#FF8B95A1'
$brDim = Get-Brush '#FF6B7480'
$brIn = Get-Brush '#FF4FC3F7'
$brOut = Get-Brush '#FFFFB74D'
$brBar = Get-Brush '#264FC3F7'
$brBadge = Get-Brush '#FF2A313B'
$brSelected = Get-Brush '#14FFFFFF'
$brDetail = Get-Brush '#0CFFFFFF'
$brTcpBg = Get-Brush '#334FC3F7'
$brUdpBg = Get-Brush '#33B39DDB'
$brUdp = Get-Brush '#FFB39DDB'
$brLanBg = Get-Brush '#3381C784'
$brLan = Get-Brush '#FF81C784'
$brRed = Get-Brush '#FFEF6B6B'
$brRedBg = Get-Brush '#33EF6B6B'
$brAmber = Get-Brush '#FFFFCA28'
$brAmberBg = Get-Brush '#33FFCA28'
$brBlue = Get-Brush '#FF64B5F6'
$brBlueBg = Get-Brush '#3364B5F6'
$brGreen = Get-Brush '#FF81C784'
$brGreenBg = Get-Brush '#2681C784'
$brLink = Get-Brush '#FF7FB8F0'

# ---------- Simgeler ----------
$iconCache = @{}
function Get-AppIcon([string]$path) {
    if (-not $path) { return $null }
    if ($iconCache.ContainsKey($path)) { return $iconCache[$path] }
    $src = $null
    try {
        $ic = [System.Drawing.Icon]::ExtractAssociatedIcon($path)
        if ($ic) {
            $src = [System.Windows.Interop.Imaging]::CreateBitmapSourceFromHIcon($ic.Handle, [System.Windows.Int32Rect]::Empty,
                [System.Windows.Media.Imaging.BitmapSizeOptions]::FromEmptyOptions())
            $src.Freeze()
            $ic.Dispose()
        }
    } catch { }
    $iconCache[$path] = $src
    return $src
}

# ---------- Satırlar ----------
function New-Column([double]$width, [switch]$Star) {
    $cd = New-Object Windows.Controls.ColumnDefinition
    if ($Star) { $cd.Width = New-Object Windows.GridLength(1, [Windows.GridUnitType]::Star) }
    else { $cd.Width = New-Object Windows.GridLength($width) }
    $cd
}

function New-Text([string]$text, [double]$size, $brush, [string]$align = 'Left') {
    $t = New-Object Windows.Controls.TextBlock
    $t.Text = $text
    $t.FontSize = $size
    $t.Foreground = $brush
    $t.HorizontalAlignment = $align
    $t.VerticalAlignment = 'Center'
    $t.TextTrimming = 'CharacterEllipsis'
    $t
}

function New-RateText([double]$rate, $brush, [double]$size) {
    $t = New-Text ([NetGadget.Sampler]::FormatRate($rate)) $size $brush 'Right'
    if ($rate -le 0) { $t.Opacity = 0.45 }
    $t
}

function Format-Age([datetime]$utc) {
    $s = [int]([DateTime]::UtcNow - $utc).TotalSeconds
    if ($s -lt 2) { 'şimdi' } else { "$s sn önce" }
}

# Tıklanınca $expanded içindeki anahtarı aç/kapatır (pencere sürüklemesini başlatmaz).
function Set-Toggle($element, [string]$key) {
    $element.Tag = $key
    $element.Cursor = [Windows.Input.Cursors]::Hand
    $element.Add_MouseLeftButtonDown({
        param($sender, $e)
        $k = [string]$sender.Tag
        if ($expanded.ContainsKey($k)) { $expanded.Remove($k) } else { $expanded[$k] = $true }
        $e.Handled = $true
        Show-Apps
    })
}

function New-Badge([string]$text, $background, $foreground) {
    $badge = New-Object Windows.Controls.Border
    $badge.CornerRadius = 3
    $badge.Padding = '0,1,0,1'
    $badge.Width = 32
    $badge.HorizontalAlignment = 'Left'
    $badge.VerticalAlignment = 'Center'
    $badge.Background = $background
    $bt = New-Text $text 8.5 $foreground 'Center'
    $bt.FontWeight = 'Bold'
    $badge.Child = $bt
    $badge
}

function New-EndpointGrid {
    $g = New-Object Windows.Controls.Grid
    $g.Margin = '0,2,0,2'
    foreach ($cd in @((New-Column 38), (New-Column -Star), (New-Column 58), (New-Column 58))) { $g.ColumnDefinitions.Add($cd) }
    $g
}

function Add-RateColumns($grid, [double]$inRate, [double]$outRate) {
    $tin = New-RateText $inRate $brIn 10.5
    $tout = New-RateText $outRate $brOut 10.5
    [Windows.Controls.Grid]::SetColumn($tin, 2)
    [Windows.Controls.Grid]::SetColumn($tout, 3)
    [void]$grid.Children.Add($tin)
    [void]$grid.Children.Add($tout)
}

# Yerel ağ keşif trafiğini (mDNS, SSDP, LLMNR...) tek satırda özetler; tıklanınca uçlar açılır.
function New-DiscoveryBlock($app, [bool]$rates) {
    $key = "$($app.Key)|lan"
    $open = $expanded.ContainsKey($key)
    $sp = New-Object Windows.Controls.StackPanel

    $g = New-EndpointGrid
    $g.Background = [Windows.Media.Brushes]::Transparent
    [void]$g.Children.Add((New-Badge 'LAN' $brLanBg $brLan))

    $inR = 0; $outR = 0; $total = 0
    foreach ($ep in $app.Discovery) { $inR += $ep.InRate; $outR += $ep.OutRate; $total += $ep.TotalIn + $ep.TotalOut }
    $names = New-Object Windows.Controls.StackPanel
    $names.Margin = '0,0,6,0'
    $chevron = if ($open) { '▾' } else { '▸' }
    [void]$names.Children.Add((New-Text "$chevron Yerel ağ keşfi" 11 $brText))
    $services = @($app.Discovery | ForEach-Object { $_.Service } | Where-Object { $_ } | Select-Object -Unique)
    $bits = @()
    if ($services.Count -gt 0) { $bits += ($services -join ', ') }
    $bits += "$($app.Discovery.Count) uç"
    if ($app.LocalDevices -gt 0) { $bits += "$($app.LocalDevices) cihaz" }
    if ($rates -and $total -gt 0) { $bits += "Σ " + [NetGadget.Sampler]::FormatBytes($total) }
    [void]$names.Children.Add((New-Text ($bits -join ' · ') 9.5 $brDim))
    [Windows.Controls.Grid]::SetColumn($names, 1)
    [void]$g.Children.Add($names)
    if ($rates) { Add-RateColumns $g $inR $outR }
    if ($rates -and ($inR + $outR) -le 0) { $g.Opacity = 0.7 }
    Set-Toggle $g $key
    [void]$sp.Children.Add($g)

    if ($open) {
        $inner = New-Object Windows.Controls.StackPanel
        $inner.Margin = '12,0,0,0'
        foreach ($ep in $app.Discovery) { [void]$inner.Children.Add((New-EndpointRow $ep $rates $app)) }
        [void]$sp.Children.Add($inner)
    }
    $sp
}

function New-Pill([string]$text, $background, $foreground) {
    $p = New-Object Windows.Controls.Border
    $p.CornerRadius = 3
    $p.Padding = '5,0,5,1'
    $p.Margin = '5,0,0,0'
    $p.VerticalAlignment = 'Center'
    $p.Background = $background
    $t = New-Text $text 9 $foreground
    $t.FontWeight = 'SemiBold'
    $p.Child = $t
    $p
}

function New-Link([string]$text, $tag) {
    $t = New-Text $text 9.5 $brLink
    $t.Margin = '0,0,12,0'
    $t.TextDecorations = [Windows.TextDecorations]::Underline
    $t.Cursor = [Windows.Input.Cursors]::Hand
    $t.Tag = $tag
    $t.Add_MouseLeftButtonDown({
        param($sender, $e)
        $e.Handled = $true
        Invoke-DetailAction $sender.Tag
    })
    $t
}

function Get-AppReport($app) {
    $lines = @("Uygulama: $($app.Name)", "Yol: $($app.Path)", "PID: $($app.Pids -join ', ')")
    if ($app.Signer -or $app.SignStatus) { $lines += "İmza: $($app.SignStatus) $($app.Signer)" }
    if ($app.ParentName) { $lines += "Başlatan: $($app.ParentName) (PID $($app.ParentPid))" }
    if ($app.User) { $lines += "Kullanıcı: $($app.User)" }
    if ($app.CommandLine) { $lines += "Komut satırı: $($app.CommandLine)" }
    if ($app.Listening.Count -gt 0) { $lines += "Dinliyor: $($app.Listening -join ', ')" }
    if ($app.Reasons.Count -gt 0) { $lines += "Şüphe puanı: $($app.Score)"; $lines += @($app.Reasons | ForEach-Object { "  $_" }) }
    $lines += 'Uç noktalar:'
    foreach ($ep in @($app.Endpoints) + @($app.Discovery)) {
        $flags = if ($ep.Flags.Count -gt 0) { ' [' + ($ep.Flags -join ', ') + ']' } else { '' }
        $lines += "  $($ep.Protocol) $($ep.Display) $($ep.Address):$($ep.Port) $($ep.Service)$flags"
    }
    $lines -join "`r`n"
}

function Invoke-DetailAction($t) {
    try {
        switch ($t.Action) {
            'trust' { $sampler.Inspector.SetAppTrust($t.App, -not $t.App.Trusted) }
            'explorer' { Start-Process explorer.exe -ArgumentList ('/select,"{0}"' -f $t.App.Path) }
            'copy' { [System.Windows.Clipboard]::SetText((Get-AppReport $t.App)) }
            'copytext' { [System.Windows.Clipboard]::SetText([string]$t.Text) }
            'eptrust' { $sampler.Inspector.SetEndpointTrust($t.App, $t.Ep, -not $t.Ep.Trusted) }
        }
    } catch { }
    Show-Apps
}

# Uç noktaya sağ tık: kopyala / bu uca güven.
function Show-EndpointMenu($t) {
    $ep = $t.Ep
    $m = New-Object System.Windows.Forms.ContextMenuStrip
    $addr = if ($ep.Address.Contains(':')) { "[$($ep.Address)]:$($ep.Port)" } else { "$($ep.Address):$($ep.Port)" }
    foreach ($text in @($ep.Display, $addr) | Select-Object -Unique) {
        $mi = New-Object System.Windows.Forms.ToolStripMenuItem("Kopyala: $text")
        $mi.Tag = @{ Action = 'copytext'; Text = $text }
        $mi.Add_Click({ param($sender, $e) Invoke-DetailAction $sender.Tag })
        [void]$m.Items.Add($mi)
    }
    if ($ep.DomainKey -and $t.App.StoreKey) {
        [void]$m.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
        $label = if ($ep.Trusted) { "Güveni kaldır ($($ep.DomainKey))" } else { "Bu uca güven ($($ep.DomainKey))" }
        $mi = New-Object System.Windows.Forms.ToolStripMenuItem($label)
        $mi.Tag = @{ Action = 'eptrust'; App = $t.App; Ep = $ep }
        $mi.Add_Click({ param($sender, $e) Invoke-DetailAction $sender.Tag })
        [void]$m.Items.Add($mi)
    }
    $m.Show([System.Windows.Forms.Cursor]::Position)
}

function New-EndpointRow($ep, [bool]$rates, $app) {
    $g = New-EndpointGrid
    $g.Background = [Windows.Media.Brushes]::Transparent
    $g.Tag = @{ App = $app; Ep = $ep }
    $g.Add_MouseRightButtonUp({
        param($sender, $e)
        $e.Handled = $true
        Show-EndpointMenu $sender.Tag
    })
    $isTcp = $ep.Protocol -eq 'TCP'
    if ($isTcp) { $badge = New-Badge 'TCP' $brTcpBg $brIn } else { $badge = New-Badge 'UDP' $brUdpBg $brUdp }
    [void]$g.Children.Add($badge)

    $names = New-Object Windows.Controls.StackPanel
    $names.Margin = '0,0,6,0'
    $host1 = New-Text $ep.Display 11 $brText
    [void]$names.Children.Add($host1)
    $bits = @()
    if ($ep.Service) { $bits += $ep.Service }
    $addr = if ($ep.Address.Contains(':')) { "[$($ep.Address)]:$($ep.Port)" } else { "$($ep.Address):$($ep.Port)" }
    $bits += if ($ep.Host) { $addr } else { "port $($ep.Port)" }
    if ($isTcp -and $ep.Count -gt 1) { $bits += "$($ep.Count) bağlantı" }
    if ($isTcp -and -not $ep.Open) { $bits += 'kapandı' }
    elseif ($ep.Total -le 0 -and ($ep.TotalIn + $ep.TotalOut) -gt 0) { $bits += Format-Age $ep.LastSeen }
    if ($rates -and ($ep.TotalIn + $ep.TotalOut) -gt 0) {
        $bits += "Σ " + [NetGadget.Sampler]::FormatBytes($ep.TotalIn + $ep.TotalOut)
    }
    [void]$names.Children.Add((New-Text ($bits -join ' · ') 9.5 $brDim))
    if ($ep.Flags.Count -gt 0) {
        $risky = -not $ep.Trusted -and @($ep.Flags | Where-Object { $_ -match '^(şüpheli port|DNS kaydı yok|internetten gelen)' }).Count -gt 0
        $fb = if ($risky) { $brAmber } elseif ($ep.Trusted) { $brGreen } else { $brBlue }
        [void]$names.Children.Add((New-Text ('⚑ ' + ($ep.Flags -join ' · ')) 9.5 $fb))
    }
    [Windows.Controls.Grid]::SetColumn($names, 1)
    [void]$g.Children.Add($names)

    if ($rates) { Add-RateColumns $g $ep.InRate $ep.OutRate }
    if ($ep.Total -le 0 -and $rates) { $g.Opacity = 0.7 }
    $g
}

function New-WrapText([string]$text, $brush, [double]$size = 9.5) {
    $t = New-Text $text $size $brush
    $t.TextTrimming = 'None'
    $t.TextWrapping = 'Wrap'
    $t
}

# İmza, başlatan süreç, komut satırı, dinlenen portlar, şüphe nedenleri ve eylemler.
function New-SecuritySection($app) {
    $sp = New-Object Windows.Controls.StackPanel
    $sp.Margin = '0,4,0,0'

    if ($app.Path) {
        switch ($app.SignStatus) {
            'Valid' { $txt = "✓ İmzalı: $($app.Signer)"; $br = $brGreen }
            'NotSigned' { $txt = '⚠ Dijital imzası yok'; $br = $brAmber }
            'Invalid' { $txt = "✗ İmza geçersiz $(if ($app.Signer) { "($($app.Signer))" })"; $br = $brRed }
            'Unknown' { $txt = 'İmza denetlenemedi'; $br = $brDim }
            default { $txt = 'İmza denetleniyor…'; $br = $brDim }
        }
        [void]$sp.Children.Add((New-Text $txt 9.5 $br))
    }

    $who = @()
    if ($app.ParentName) { $who += "Başlatan: $($app.ParentName) (PID $($app.ParentPid))" }
    if ($app.User) { $who += "Kullanıcı: $($app.User)" }
    if ($app.StartTime -gt [datetime]'2000-01-01') { $who += "Başladı: $($app.StartTime.ToString('dd.MM HH:mm'))" }
    if ($who.Count -gt 0) { [void]$sp.Children.Add((New-WrapText ($who -join ' · ') $brSub)) }

    if ($app.CommandLine) {
        $cl = $app.CommandLine
        if ($cl.Length -gt 400) { $cl = $cl.Substring(0, 400) + '…' }
        [void]$sp.Children.Add((New-WrapText "Komut satırı: $cl" $brDim))
    }
    if ($app.Listening.Count -gt 0) {
        [void]$sp.Children.Add((New-WrapText "Bağlantı bekliyor (TCP): $($app.Listening -join ', ')" $brSub))
    }

    if ($app.Reasons.Count -gt 0) {
        $head = "Neden işaretlendi (şüphe puanı $($app.Score)):"
        if ($app.Trusted) { $head = 'Güvenilir olarak işaretli — aşağıdaki işaretler puana katılmıyor:' }
        $hb = if ($app.Trusted) { $brGreen } elseif ($app.Level -ge 3) { $brRed } elseif ($app.Level -ge 2) { $brAmber } else { $brSub }
        $h = New-Text $head 9.5 $hb
        $h.Margin = '0,4,0,1'
        $h.FontWeight = 'SemiBold'
        [void]$sp.Children.Add($h)
        foreach ($r in $app.Reasons) { [void]$sp.Children.Add((New-WrapText "  $r" $(if ($app.Trusted) { $brDim } else { $brText }))) }
    }

    $links = New-Object Windows.Controls.WrapPanel
    $links.Margin = '0,5,0,0'
    if ($app.StoreKey) {
        $label = if ($app.Trusted) { 'Güveni kaldır' } else { 'Güvenilir olarak işaretle' }
        [void]$links.Children.Add((New-Link $label @{ Action = 'trust'; App = $app }))
    }
    if ($app.Path) { [void]$links.Children.Add((New-Link "Gezgin'de göster" @{ Action = 'explorer'; App = $app })) }
    [void]$links.Children.Add((New-Link 'Bilgileri kopyala' @{ Action = 'copy'; App = $app }))
    [void]$sp.Children.Add($links)
    $sp
}

function New-DetailPanel($app, [bool]$rates) {
    $b = New-Object Windows.Controls.Border
    $b.Background = $brDetail
    $b.CornerRadius = 6
    $b.Margin = '4,0,0,6'
    $b.Padding = '10,6,6,6'
    $sp = New-Object Windows.Controls.StackPanel

    $info = @("PID $($app.Pids -join ', ')")
    if ($rates) {
        $info += "oturumda ↓ $([NetGadget.Sampler]::FormatBytes($app.TotalIn))  ↑ $([NetGadget.Sampler]::FormatBytes($app.TotalOut))"
    }
    [void]$sp.Children.Add((New-Text ($info -join ' · ') 9.5 $brSub))
    foreach ($line in @($app.Services, $app.Path)) {
        if (-not $line) { continue }
        [void]$sp.Children.Add((New-WrapText $line $brDim))
    }
    [void]$sp.Children.Add((New-SecuritySection $app))

    $sep = New-Object Windows.Controls.Border
    $sep.Height = 1
    $sep.Background = $brDetail
    $sep.Margin = '0,5,0,3'
    [void]$sp.Children.Add($sep)

    if ($app.Endpoints.Count -eq 0 -and $app.Discovery.Count -eq 0) { [void]$sp.Children.Add((New-Text 'Uç nokta yok.' 10 $brDim)) }
    foreach ($ep in $app.Endpoints) { [void]$sp.Children.Add((New-EndpointRow $ep $rates $app)) }
    if ($app.Discovery.Count -gt 0) { [void]$sp.Children.Add((New-DiscoveryBlock $app $rates)) }

    $b.Child = $sp
    $b
}

function New-AppRow($app, [double]$maxTotal, [double]$rowWidth, [bool]$rates, [bool]$open) {
    $outer = New-Object Windows.Controls.Grid
    $outer.Margin = '0,1,0,1'
    $outer.Background = if ($open) { $brSelected } else { [Windows.Media.Brushes]::Transparent }

    if ($rates -and $app.Total -gt 0 -and $maxTotal -gt 0) {
        $bar = New-Object Windows.Controls.Border
        $bar.Background = $brBar
        $bar.CornerRadius = 5
        $bar.HorizontalAlignment = 'Left'
        $bar.Width = [Math]::Max(4, $rowWidth * [Math]::Sqrt($app.Total / $maxTotal))
        [void]$outer.Children.Add($bar)
    }

    $g = New-Object Windows.Controls.Grid
    $g.Margin = '6,4,6,4'
    foreach ($cd in @((New-Column 26), (New-Column -Star), (New-Column 62), (New-Column 62))) { $g.ColumnDefinitions.Add($cd) }

    $icon = Get-AppIcon $app.Path
    if ($icon) {
        $img = New-Object Windows.Controls.Image
        $img.Source = $icon
        $img.Width = 18; $img.Height = 18
        $img.HorizontalAlignment = 'Left'
        [Windows.Media.RenderOptions]::SetBitmapScalingMode($img, 'HighQuality')
        [void]$g.Children.Add($img)
    } else {
        $badge = New-Object Windows.Controls.Border
        $badge.Width = 18; $badge.Height = 18; $badge.CornerRadius = 4
        $badge.HorizontalAlignment = 'Left'
        $badge.Background = $brBadge
        $letter = if ($app.Name) { $app.Name.Substring(0, 1).ToUpper() } else { '?' }
        $badge.Child = New-Text $letter 10 $brSub 'Center'
        [void]$g.Children.Add($badge)
    }

    $names = New-Object Windows.Controls.StackPanel
    $names.VerticalAlignment = 'Center'
    $names.Margin = '0,0,6,0'
    $titleRow = New-Object Windows.Controls.DockPanel
    $pills = @()
    if ($app.Trusted) { $pills += New-Pill 'güvenilir' $brGreenBg $brGreen }
    elseif ($app.Level -ge 3) { $pills += New-Pill 'şüpheli' $brRedBg $brRed }
    elseif ($app.Level -ge 2) { $pills += New-Pill 'dikkat' $brAmberBg $brAmber }
    if ($app.IsNew) { $pills += New-Pill 'yeni' $brBlueBg $brBlue }
    foreach ($p in $pills) {
        [Windows.Controls.DockPanel]::SetDock($p, 'Right')
        [void]$titleRow.Children.Add($p)
    }
    $title = New-Text $app.Name 12.5 $brText
    $title.FontWeight = 'SemiBold'
    [void]$titleRow.Children.Add($title)
    [void]$names.Children.Add($titleRow)
    $bits = @()
    if ($app.TcpCount -gt 0) { $bits += "TCP $($app.TcpCount)" }
    if ($app.UdpCount -gt 0) { $bits += "UDP $($app.UdpCount)" }
    if ($app.Discovery.Count -gt 0) { $bits += "yerel ağ $($app.Discovery.Count)" }
    if ($settings.SortBy -eq 'Session' -and $rates) {
        $bits += "Σ " + [NetGadget.Sampler]::FormatBytes($app.TotalIn + $app.TotalOut)
    }
    if ($app.Endpoints.Count -gt 0) { $bits += $app.Endpoints[0].Display }
    [void]$names.Children.Add((New-Text ($bits -join ' · ') 10 $brSub))
    [Windows.Controls.Grid]::SetColumn($names, 1)
    [void]$g.Children.Add($names)

    if ($rates) {
        $tin = New-RateText $app.InRate $brIn 11.5
        $tout = New-RateText $app.OutRate $brOut 11.5
        [Windows.Controls.Grid]::SetColumn($tin, 2)
        [Windows.Controls.Grid]::SetColumn($tout, 3)
        [void]$g.Children.Add($tin)
        [void]$g.Children.Add($tout)
    }
    [void]$outer.Children.Add($g)
    if ($rates -and $app.Total -le 0 -and -not $open) { $outer.Opacity = 0.6 }

    Set-Toggle $outer $app.Key

    $wrap = New-Object Windows.Controls.StackPanel
    [void]$wrap.Children.Add($outer)
    if ($open) { [void]$wrap.Children.Add((New-DetailPanel $app $rates)) }
    $wrap
}

# ---------- Güncelleme ----------
# Test anlık görüntüsünde "ilk kez görüldü" / güvenilir listesi dosyalarına dokunulmaz.
$dataDir = if ($Snapshot) { $null } else { $settingsDir }
$sampler = New-Object NetGadget.Sampler -ArgumentList ([bool]$isAdmin), $dataDir
$sampler.ReverseDns = [bool]$settings.ReverseDns
$sampler.LocalNames = [bool]$settings.LocalNames
$histIn = New-Object 'System.Collections.Generic.List[double]'
$histOut = New-Object 'System.Collections.Generic.List[double]'
$HistLen = 60
$expanded = @{}
$script:lastApps = @()

function Test-RatesMode { $sampler.EtwActive -or ($isAdmin -and -not $sampler.EStatsDenied) }

function Update-Graph {
    $w = $ui.Graph.ActualWidth
    $h = $ui.Graph.ActualHeight
    if ($w -le 0 -or $h -le 0) { return }
    $max = 8KB
    foreach ($v in $histIn) { if ($v -gt $max) { $max = $v } }
    foreach ($v in $histOut) { if ($v -gt $max) { $max = $v } }
    $step = $w / ($HistLen - 1)
    foreach ($pair in @(@($histIn, $ui.LineIn), @($histOut, $ui.LineOut))) {
        $hist = $pair[0]
        $pts = New-Object Windows.Media.PointCollection
        for ($i = 0; $i -lt $hist.Count; $i++) {
            $x = $w - ($hist.Count - 1 - $i) * $step
            $y = $h - 1 - ($hist[$i] / $max) * ($h - 3)
            $pts.Add((New-Object Windows.Point($x, $y)))
        }
        $pair[1].Points = $pts
    }
}

function Show-Apps {
    $apps = $script:lastApps
    $rates = Test-RatesMode

    $shown = @($apps)
    if ($rates -and $settings.ActiveOnly) { $shown = @($apps | Where-Object { $_.Total -gt 0 -or $expanded.ContainsKey($_.Key) }) }

    # Örnekleyici listeyi zaten anlık hıza göre sıralı verir.
    $sortKeys = switch ($settings.SortBy) {
        'Session' { @{ Expression = { $_.TotalIn + $_.TotalOut }; Descending = $true }, @{ Expression = 'Total'; Descending = $true }, 'Name' }
        'Connections' { @{ Expression = 'Connections'; Descending = $true }, @{ Expression = 'Total'; Descending = $true }, 'Name' }
        'Name' { 'Name' }
        'Risk' { @{ Expression = 'Score'; Descending = $true }, @{ Expression = 'Total'; Descending = $true }, 'Name' }
    }
    if ($sortKeys) { $shown = @($shown | Sort-Object -Property $sortKeys) }
    $ui.HeadApp.Text = 'UYGULAMA · ' + $SortModes[[string]$settings.SortBy].Head.ToUpper()

    $maxTotal = 0
    foreach ($a in $shown) { if ($a.Total -gt $maxTotal) { $maxTotal = $a.Total } }

    $ui.List.Children.Clear()
    $rowWidth = $ui.List.ActualWidth
    foreach ($a in ($shown | Select-Object -First ([int]$settings.MaxRows))) {
        [void]$ui.List.Children.Add((New-AppRow $a $maxTotal $rowWidth $rates $expanded.ContainsKey($a.Key)))
    }
    if ($shown.Count -eq 0) {
        $msg = if ($apps.Count -gt 0) { 'Şu an veri aktaran uygulama yok.' } else { 'Şu an internete bağlı uygulama yok.' }
        $empty = New-Text $msg 11.5 $brSub
        $empty.Margin = '6,6,0,6'
        [void]$ui.List.Children.Add($empty)
    }

    $summary = "$($apps.Count) uygulama · TCP $($sampler.TcpCount)"
    if ($sampler.EtwActive) { $summary += " · UDP $($sampler.UdpCount) · yerel ağ $($sampler.LocalCount)" }
    if ($shown.Count -gt [int]$settings.MaxRows) { $summary += " · +$($shown.Count - [int]$settings.MaxRows) daha" }
    $flagged = @($apps | Where-Object { $_.Level -ge 2 }).Count
    if ($flagged -gt 0) { $summary += " · ⚠ $flagged dikkat gerektiren" }
    $summary += ' · ayrıntı için uygulamaya tıklayın'
    $learn = $sampler.Inspector.LearningRemaining
    if ($learn.TotalSeconds -gt 0) {
        $summary += "`nÖğrenme modu: normal trafiğiniz öğreniliyor, $([Math]::Ceiling($learn.TotalMinutes)) dk boyunca hiçbir şey 'yeni' sayılmaz."
    }
    if (-not $rates) { $summary += "`nUygulama başına trafik için yönetici izniyle başlatın." }
    elseif (-not $sampler.EtwActive) { $summary += "`nUDP izlenemiyor ($($sampler.EtwError)); yalnızca TCP hızları." }
    $ui.Footer.Text = $summary
}

function Update-View {
    try {
        $script:lastApps = @($sampler.Sample())

        $histIn.Add($sampler.TotalIn); $histOut.Add($sampler.TotalOut)
        while ($histIn.Count -gt $HistLen) { $histIn.RemoveAt(0); $histOut.RemoveAt(0) }
        $ui.TotalIn.Text = [NetGadget.Sampler]::FormatRate($sampler.TotalIn)
        $ui.TotalOut.Text = [NetGadget.Sampler]::FormatRate($sampler.TotalOut)
        Update-Graph

        $rates = Test-RatesMode
        $ui.ModeText.Text = if ($sampler.EtwActive) { 'canlı · TCP + UDP' } elseif ($rates) { 'canlı · yalnızca TCP' } else { 'sınırlı mod' }
        $ui.ModeText.ToolTip = if ($sampler.EtwActive) { "ETW ağ olayı: $($sampler.EventCount)" } else { $null }
        $ui.HeadIn.Visibility = if ($rates) { 'Visible' } else { 'Hidden' }
        $ui.HeadOut.Visibility = $ui.HeadIn.Visibility

        Show-Apps
        $ui.LiveDot.Opacity = if ($ui.LiveDot.Opacity -lt 1) { 1 } else { 0.55 }
    } catch {
        $ui.Footer.Text = 'Hata: ' + $_.Exception.Message
    }
}

# ---------- Test anlık görüntüsü ----------
if ($Snapshot) {
    $window.Left = -5000; $window.Top = 0
    $window.Show()
    for ($i = 0; $i -lt 6; $i++) { Update-View; $window.UpdateLayout(); Start-Sleep -Milliseconds 1000 }
    if ($script:lastApps.Count -gt 0) { $expanded[$script:lastApps[0].Key] = $true }
    Update-View
    $window.UpdateLayout()
    $root = $ui.Root
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]($root.ActualWidth + 8), [int]($root.ActualHeight + 8), 96, 96,
        [Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($window.Content)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $fs = [IO.File]::Create($Snapshot)
    try { $enc.Save($fs) } finally { $fs.Dispose() }
    $window.Close()
    $sampler.Dispose()
    exit
}

# ---------- Konum ----------
$work = [System.Windows.SystemParameters]::WorkArea
$vl = [System.Windows.SystemParameters]::VirtualScreenLeft
$vt = [System.Windows.SystemParameters]::VirtualScreenTop
$vw = [System.Windows.SystemParameters]::VirtualScreenWidth
$vh = [System.Windows.SystemParameters]::VirtualScreenHeight
$left = $work.Right - $window.Width - 16
$top = $work.Top + 16
if ($null -ne $settings.Left -and $null -ne $settings.Top) {
    $sl = [double]$settings.Left; $st = [double]$settings.Top
    if ($sl -ge $vl - 100 -and $sl -le $vl + $vw - 100 -and $st -ge $vt -and $st -le $vt + $vh - 60) { $left = $sl; $top = $st }
}
$window.Left = $left
$window.Top = $top
$window.Topmost = [bool]$settings.Topmost

# ---------- Tepsi simgesi ve menü ----------
$bmp = New-Object System.Drawing.Bitmap 32, 32
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.SmoothingMode = 'AntiAlias'
$gfx.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 21, 25, 31))), 0, 0, 31, 31)
$penIn = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 79, 195, 247)), 3.5
$penOut = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 183, 77)), 3.5
$gfx.DrawLine($penIn, 11, 7, 11, 24); $gfx.DrawLine($penIn, 6, 19, 11, 24); $gfx.DrawLine($penIn, 16, 19, 11, 24)
$gfx.DrawLine($penOut, 21, 25, 21, 8); $gfx.DrawLine($penOut, 16, 13, 21, 8); $gfx.DrawLine($penOut, 26, 13, 21, 8)
$gfx.Dispose()
$trayIconImage = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miShow = New-Object System.Windows.Forms.ToolStripMenuItem('Göster / Gizle')
$miTop = New-Object System.Windows.Forms.ToolStripMenuItem('Her zaman üstte')
$miTop.CheckOnClick = $true
$miTop.Checked = [bool]$settings.Topmost
$miActive = New-Object System.Windows.Forms.ToolStripMenuItem('Yalnızca veri aktaranları göster')
$miActive.CheckOnClick = $true
$miActive.Checked = [bool]$settings.ActiveOnly
$miActive.Enabled = $isAdmin
$miCollapse = New-Object System.Windows.Forms.ToolStripMenuItem('Tüm ayrıntıları kapat')

$miSort = New-Object System.Windows.Forms.ToolStripMenuItem('Sıralama')
function Set-SortMode([string]$mode) {
    $settings.SortBy = $mode
    foreach ($item in $miSort.DropDownItems) { $item.Checked = ([string]$item.Tag -eq $mode) }
    Save-Settings
    Show-Apps
}
foreach ($mode in $SortModes.Keys) {
    $item = New-Object System.Windows.Forms.ToolStripMenuItem($SortModes[$mode].Label)
    $item.Tag = $mode
    $item.Checked = ($mode -eq $settings.SortBy)
    $item.Add_Click({ param($sender, $e) Set-SortMode ([string]$sender.Tag) })
    [void]$miSort.DropDownItems.Add($item)
}

$miNames = New-Object System.Windows.Forms.ToolStripMenuItem('Ad çözümleme')
$miReverse = New-Object System.Windows.Forms.ToolStripMenuItem('İnternet adresleri için ters DNS')
$miReverse.CheckOnClick = $true
$miReverse.Checked = [bool]$settings.ReverseDns
$miReverse.ToolTipText = 'Alan adı bilinmeyen IP''ler için yalnızca DNS üzerinden PTR sorgusu yapar (çok küçük DNS trafiği).'
$miLocal = New-Object System.Windows.Forms.ToolStripMenuItem('Yerel cihaz adlarını çöz (NetBIOS/mDNS)')
$miLocal.CheckOnClick = $true
$miLocal.Checked = [bool]$settings.LocalNames
$miLocal.ToolTipText = 'Yerel ağdaki cihazların adlarını gösterir; bunun için yerel ağda NetBIOS/mDNS sorgusu gönderir.'
[void]$miNames.DropDownItems.AddRange(@($miReverse, $miLocal))

$miExit = New-Object System.Windows.Forms.ToolStripMenuItem('Kapat')
[void]$menu.Items.AddRange(@($miShow, $miTop, $miActive, $miSort, $miCollapse, $miNames,(New-Object System.Windows.Forms.ToolStripSeparator), $miExit))

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = $trayIconImage
$tray.Text = 'Ağ Etkinliği'
$tray.ContextMenuStrip = $menu
$tray.Visible = $true

function Switch-Visibility {
    if ($window.IsVisible) { $window.Hide() } else { $window.Show(); [void]$window.Activate() }
}

$miShow.Add_Click({ Switch-Visibility })
$tray.Add_MouseDoubleClick({ Switch-Visibility })
$miTop.Add_Click({ $window.Topmost = $miTop.Checked; $settings.Topmost = $miTop.Checked; Save-Settings })
$miActive.Add_Click({ $settings.ActiveOnly = $miActive.Checked; Save-Settings; Show-Apps })
$miCollapse.Add_Click({ $expanded.Clear(); Show-Apps })
$miReverse.Add_Click({ $settings.ReverseDns = $miReverse.Checked; $sampler.ReverseDns = $miReverse.Checked; Save-Settings })
$miLocal.Add_Click({ $settings.LocalNames = $miLocal.Checked; $sampler.LocalNames = $miLocal.Checked; Save-Settings })
$miExit.Add_Click({ $window.Close() })

$window.Add_MouseLeftButtonDown({ try { $window.DragMove() } catch { } })
$window.Add_LocationChanged({ $settings.Left = [Math]::Round($window.Left); $settings.Top = [Math]::Round($window.Top) })
$window.Add_MouseLeftButtonUp({ Save-Settings })
$window.Add_MouseRightButtonUp({ $menu.Show([System.Windows.Forms.Cursor]::Position) })

$app = New-Object System.Windows.Application
$app.ShutdownMode = 'OnExplicitShutdown'

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(1)
$timer.Add_Tick({ Update-View })

$window.Add_Closed({
    $timer.Stop()
    Save-Settings
    $sampler.Dispose()
    $tray.Visible = $false
    $tray.Dispose()
    $app.Shutdown()
})

$window.Show()
Update-View
$timer.Start()
[void]$app.Run()
$mutex.ReleaseMutex()
