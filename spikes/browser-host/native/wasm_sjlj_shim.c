// The workload's clang (LLVM 19) lowers setjmp/longjmp with wasm EH to the
// newer ABI, __wasm_setjmp / __wasm_setjmp_test, but the Emscripten 3.1.56
// libcompiler_rt-wasm-sjlj it links against only has the older
// saveSetjmp/testSetjmp. FreeType's smooth rasterizer and ftobjs setjmp, so
// supply the two, as later Emscripten does (system/lib/compiler-rt/
// emscripten_setjmp.c). __wasm_longjmp, which throws {env, val}, is the
// library's own and is unchanged between the two ABIs.
#include <stdint.h>

struct jmp_buf_impl {
  void* func_invocation_id;
  uint32_t label;
};

void __wasm_setjmp(void* env, uint32_t label, void* func_invocation_id) {
  struct jmp_buf_impl* jb = env;
  jb->func_invocation_id = func_invocation_id;
  jb->label = label;
}

uint32_t __wasm_setjmp_test(void* env, void* func_invocation_id) {
  struct jmp_buf_impl* jb = env;
  return jb->func_invocation_id == func_invocation_id ? jb->label : 0;
}
