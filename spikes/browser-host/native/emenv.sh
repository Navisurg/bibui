# Points emcc at the Emscripten 3.1.56 the .NET wasm-tools workload ships, so
# the static libraries are built by the same toolchain that links the app.
: "${DOTNET_ROOT:=$HOME/.dotnet-wasm}"
_v=$(ls "$DOTNET_ROOT/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64" | sort -V | tail -1)
_sdk="$DOTNET_ROOT/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64/$_v/tools"
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$_sdk/bin"
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$_sdk"
export DOTNET_EMSCRIPTEN_NODE_JS="$DOTNET_ROOT/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Node.linux-x64/$_v/tools/bin/node"
export EM_CACHE="$DOTNET_ROOT/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Cache.linux-x64/$_v/tools/emscripten/cache"
export PATH="$_sdk/emscripten:$PATH"
