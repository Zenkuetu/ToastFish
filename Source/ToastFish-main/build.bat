@echo off
cd /d "E:\ToastFish.v3.0\Source\ToastFish-main"
C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe ToastFish.csproj /p:Configuration=Release /p:Platform="AnyCPU" /v:minimal
