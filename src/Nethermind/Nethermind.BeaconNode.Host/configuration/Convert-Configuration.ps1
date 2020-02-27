function formatSectionName($value) {
  if ($value -eq 'Misc') { return 'MiscellaneousParameters'; }
  if ($value -eq 'State vector lengths') { return 'StateListLengths'; }
  if ($value -eq 'Reward and penalty quotients') { return 'RewardsAndPenalties'; }
  $words = $value -Split ' ';
  $cased = $words | % { $_.Substring(0,1).ToUpper() + $_.Substring(1).ToLower() };
  return $cased -Join '';
}

function formatSettingName($value) {
  $words = $value -Split '_';
  $expanded = $words | % { 
    if ($_ -eq 'Min') { return 'Minimum'; }
    if ($_ -eq 'Max') { return 'Maximum'; }
    return $_;
  }
  $cased = $expanded | % { $_.Substring(0,1).ToUpper() + $_.Substring(1).ToLower() };
  return $cased -Join '';
}

function convert($sourceYaml, $destinationJson) {
  $jsonRoot = New-Object Object;
  $parent = New-Object Object;
  $jsonRoot | Add-Member -NotePropertyName 'BeaconChain' -NotePropertyValue $parent;

  $content = Get-Content $sourceYaml;
  $content | ForEach-Object {
    $line = $_;

    if ($line) {
      if ($line.StartsWith('#')) {
        if ($line.StartsWith('# ----')) {
          $sectionName = formatSectionName $previousComment.Substring(1).Trim();
          $section = New-Object Object;
          $parent | Add-Member -NotePropertyName $sectionName -NotePropertyValue $section;
        } else {
          $previousComment = $line;
        }
      } else {
        $parts = $line -Split ':';
        if ($parts.Length -eq 2) {
          $settingName = formatSettingName $parts[0].Trim();
          $settingValue = $parts[1].Trim();
          if ($settingValue -match '^\d+$') {
            $settingValue = [UInt64]$settingValue;
          }
          $section | Add-Member -NotePropertyName $settingName -NotePropertyValue $settingValue;
        }
      }
    }
  }

  $jsonRoot | ConvertTo-Json | Out-File $destinationJson
}

if (-not (Test-Path 'Output')) { mkdir 'Output'; }
convert 'mainnet.yaml' 'Output/Production/appsettings-config.json';
convert 'minimal.yaml' 'Output/Development/appsettings-config.json';
