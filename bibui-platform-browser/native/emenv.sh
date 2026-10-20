# Sourced by build.sh: puts the emcc of the .NET wasm-tools workload on PATH,
# so the archives are built by the same Emscripten that links the app.
# DOTNET_ROOT is the SDK the workload is installed in; without it, the one
# `dotnet` on PATH runs from.
if [[ -z "${DOTNET_ROOT:-}" ]]; then
  DOTNET_ROOT=$(dirname "$(readlink -f "$(command -v dotnet)")")
fi
_pack="$DOTNET_ROOT/packs/Microsoft.NET.Runtime.Emscripten.$EMSCRIPTEN_VERSION"
if ! compgen -G "$_pack.Sdk.*/*/tools" > /dev/null; then
  echo "No Emscripten $EMSCRIPTEN_VERSION under $DOTNET_ROOT/packs: install the workload first (dotnet workload install wasm-tools)." >&2
  return 1
fi
_sdk=$(ls -d "$_pack".Sdk.*/*/tools | sort -V | tail -1)
_node=$(ls -d "$_pack".Node.*/*/tools/bin/node | sort -V | tail -1)
_cache=$(ls -d "$_pack".Cache.*/*/tools/emscripten/cache | sort -V | tail -1)
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$_sdk/bin"
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$_sdk"
export DOTNET_EMSCRIPTEN_NODE_JS="$_node"
export EM_CACHE="$_cache"
export PATH="$_sdk/emscripten:$PATH"
unset _pack _sdk _node _cache
