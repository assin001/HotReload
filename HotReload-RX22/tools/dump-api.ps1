param(
    [string]$GameDir = 'E:\game\GDAkk\Koikatsu BetterRepack RX22',
    [string]$TypeLike = '*',
    [string]$MemberLike = '*',
    [string]$AssemblyPath = '',
    [string]$Method = '',
    [switch]$Il,
    [switch]$Qualify
)

Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')

if (-not $AssemblyPath) {
    $AssemblyPath = Join-Path $GameDir 'Koikatu_Data\Managed\Assembly-CSharp.dll'
}
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AssemblyPath)

function Dump-Il {
    param($MethodDef)
    Write-Output ('--- IL ' + $MethodDef.DeclaringType.FullName + '::' + $MethodDef.Name)
    if (-not $MethodDef.HasBody) { Write-Output '  (no body)'; return }
    foreach ($i in $MethodDef.Body.Instructions) {
        $op = $i.OpCode.Name
        $operand = ''
        if ($i.Operand -ne $null) { $operand = $i.Operand.ToString() }
        if ($op -match 'call|newobj|ldstr|ldfld|stfld|ldsfld|stsfld|isinst|castclass|ret') {
            Write-Output ('  ' + $op + ' ' + $operand)
        }
    }
}

function Dump-Type {
    param($Type)
    $name = $Type.FullName
    if ($name -like $TypeLike) {
        Write-Output ('=== ' + $name)
        foreach ($f in $Type.Fields) {
            if ($f.Name -like $MemberLike) {
                $flags = ''
                if ($f.IsStatic) { $flags += 'static ' }
                if ($f.IsPublic) { $flags += 'public' } elseif ($f.IsFamily) { $flags += 'protected' } else { $flags += 'private' }
                $prefix = ''
                if ($Qualify) { $prefix = $name + '  ' }
                Write-Output ('  ' + $prefix + 'F [' + $flags + '] ' + $f.FieldType.FullName + ' ' + $f.Name)
            }
        }
        foreach ($p in $Type.Properties) {
            if ($p.Name -like $MemberLike) {
                $pprefix = ''
                if ($Qualify) { $pprefix = $name + '  ' }
                Write-Output ('  ' + $pprefix + 'P ' + $p.PropertyType.FullName + ' ' + $p.Name)
            }
        }
        foreach ($m in $Type.Methods) {
            if ($m.Name -like $MemberLike) {
                $gp = ''
                if ($m.HasGenericParameters) {
                    $gp = '<' + (($m.GenericParameters | ForEach-Object { $_.Name }) -join ',') + '>'
                }
                $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.FullName + ' ' + $_.Name }) -join ', '
                Write-Output ('  M ' + $m.ReturnType.FullName + ' ' + $m.Name + $gp + '(' + $ps + ')')

                if ($Il -and $Method -and $m.Name -eq $Method) { Dump-Il $m }
            }
        }
    }
    foreach ($n in $Type.NestedTypes) { Dump-Type $n }
}

foreach ($t in $asm.MainModule.Types) { Dump-Type $t }
