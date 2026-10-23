@echo off
rem Gera o GLM INFRA LUFT pronto para levar a outro PC (nao precisa instalar o .NET la).
rem Saida: ..\GLMInfraLuft-dist\GLMInfraLuft.exe + LEIA-ME.txt
rem Sempre recompila do zero a partir do codigo atual desta pasta.
cd /d "%~dp0"
echo Fechando o GLM INFRA LUFT se estiver aberto ...
taskkill /f /im GLMInfraLuft.exe >nul 2>&1
echo Gerando GLMInfraLuft.exe a partir do codigo atual ...
rmdir /s /q obj\Release >nul 2>&1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "..\GLMInfraLuft-dist" -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU. Veja os erros acima. O .exe antigo NAO foi atualizado.
    pause
    exit /b 1
)
copy /y "LEIA-ME.txt" "..\GLMInfraLuft-dist\LEIA-ME.txt" >nul
echo.
echo Pronto: %~dp0..\GLMInfraLuft-dist
for %%F in ("..\GLMInfraLuft-dist\GLMInfraLuft.exe") do echo Arquivo gerado em %%~tF (confira se e de agora^)
pause
