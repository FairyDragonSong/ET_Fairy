param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..\Unity')
)

$ErrorActionPreference = 'Stop'

$cecil = Get-ChildItem (Join-Path $ProjectRoot 'Library\PackageCache') -Recurse -Filter Mono.Cecil.dll |
    Where-Object FullName -Match 'com\.unity\.nuget\.mono-cecil@' |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $cecil) {
    throw 'Mono.Cecil.dll was not found. Open the Unity project once so PackageCache is restored.'
}

Add-Type -Path $cecil

$assemblyPath = Join-Path $ProjectRoot 'Assets\Plugins\Sirenix\Assemblies\Sirenix.Reflection.Editor.dll'
$temporaryPath = "$assemblyPath.compat.tmp"
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)

try {
    $bridge = $assembly.MainModule.Types |
        Where-Object FullName -eq 'Sirenix.Reflection.Editor.UIElementsUtility_Internals'

    if (-not $bridge) {
        throw 'The Odin UIElementsUtility bridge type was not found.'
    }

    $methodNames = @(
        'add_BeginContainerCallback',
        'remove_BeginContainerCallback',
        'add_EndContainerCallback',
        'remove_EndContainerCallback'
    )

    foreach ($name in $methodNames) {
        $method = $bridge.Methods | Where-Object Name -eq $name
        if (-not $method) {
            throw "The Odin bridge method '$name' was not found."
        }

        $method.Body.ExceptionHandlers.Clear()
        $method.Body.Variables.Clear()
        $method.Body.Instructions.Clear()
        $method.Body.Instructions.Add(
            [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret)
        )
    }

    $assembly.Write($temporaryPath)
}
finally {
    $assembly.Dispose()
}

Move-Item -Force $temporaryPath $assemblyPath
Write-Host "Patched Odin's obsolete UI Toolkit container callbacks for Unity 6000.5+."
