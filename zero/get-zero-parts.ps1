# Fetch R-ZERO prerequisites into E:\act2\repo\zero-parts\
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
$parts = "E:\act2\repo\zero-parts"
New-Item -ItemType Directory -Force -Path $parts | Out-Null

# 1) llama.cpp ubuntu-x64 release (grab latest tag from the releases page)
$rel = Invoke-RestMethod "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=1"
$asset = $rel[0].assets | Where-Object { $_.name -like "*bin-ubuntu-x64.zip" } | Select-Object -First 1
Write-Host "downloading $($asset.name)"
Invoke-WebRequest $asset.browser_download_url -OutFile "$env:TEMP\llama-ubuntu.zip"
Expand-Archive "$env:TEMP\llama-ubuntu.zip" -DestinationPath "$env:TEMP\llama-rel" -Force
Copy-Item "$env:TEMP\llama-rel\build\bin\llama-server" $parts -Force
Copy-Item "$env:TEMP\llama-rel\build\bin\*.so" $parts -Force -ErrorAction SilentlyContinue

# 2) model: Qwen2.5-3B-Instruct q4_k_m — if LM Studio already has it, copy from
#    there; otherwise grab from HF (public, no auth for this repo):
$lms = Get-ChildItem "$env:USERPROFILE\.lmstudio\models" -Recurse -Filter "*wen*3*nstruct*q4*.gguf" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($lms) { Copy-Item $lms.FullName "$parts\model.gguf" -Force; Write-Host "model from LM Studio: $($lms.Name)" }
else {
  Write-Host "downloading Qwen2.5-3B-Instruct-Q4_K_M.gguf from HF (~2GB)"
  Invoke-WebRequest "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf" -OutFile "$parts\model.gguf"
}
Write-Host "zero-parts ready:"; Get-ChildItem $parts | Format-Table Name, @{n="MB";e={[int]($_.Length/1MB)}}
