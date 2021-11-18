$nukeCache = $true
$testProjectName = 'hdrp-empty'
$unityPath = 'C:\builds\editor\2022.1.0a15-49120632d05b'
$template = 'com.unity.template.hdrp-blank-3.0.2.tgz'


function killit($what) {
    if (get-process -ea:silent $what) {
        pskill -nobanner $what
    }
}

function killall {
    killit unity
    killit unitypackagemanager
    killit "unity hub"
}

function killdir($where) {
    if (test-path $where) {
        del -r $where
    }
}

$testProjectPath = "c:\temp\$testProjectName"

Write-Host "*** Killing processes"
killall

Write-Host "*** Deleting old test folder"
killdir $testProjectPath
mkdir $testProjectPath >$null

if ($nukeCache) {
    Write-Host "*** Nuking Unity global cache"
    killdir $Env:LOCALAPPDATA C:\Users\scott\AppData\Local\Unity\cache
}

Write-Host "*** Starting up Procmon"
sudo procmon /accepteula /backingfile $testProjectPath\events.pml /loadconfig $PSScriptRoot\config.pmc /profiling /minimized /quiet
Write-Host "*** Waiting a bit for it to get going"
sleep 5

Write-Host "*** Starting up Unity"
$Env:UNITY_MIXED_CALLSTACK = 1
$Env:UNITY_EXT_LOGGING = 1
& "$unityPath\Unity.exe" -logFile $testProjectPath\editor.log -createproject $testProjectPath\project -cloneFromTemplate $env:APPDATA\unityhub\templates\$template

# TODO: have unity run script that waits until loaded then copies its pmip and shuts down, kills procmon too

Write-Host -nonew ">>> Press any key when Unity done: "
[console]::ReadKey() >$null
Write-Host

Write-Host "*** Saving pmip log"
if (dir $env:APPDATA\Temp\*pmip*) {
    copy $env:APPDATA\Temp\*pmip* $testProjectPath\
}
else {
    write-error "Unity was killed before I could save the pmip log!!"
}

Write-Host "*** Killing processes"
killall
sudo procmon /terminate
