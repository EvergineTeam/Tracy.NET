// Translation unit used by TracyGen instead of including <tracy/TracyC.h> directly.
//
// TRACY_ENABLE is the whole API switch: without it TracyC.h declares no functions at all,
// only no-op macros, so a parse of the bare header would silently produce an empty binding.
// The generator fails when zero functions come out, but defining it here is what makes the
// real surface exist in the first place. The shipped natives are compiled with the same
// define (plus TRACY_ON_DEMAND, which changes behaviour but not the exported surface).
//
// CppAst always drives libclang in C++ mode. TracyC.h is C++-clean (its extern "C" block
// activates under __cplusplus), so unlike MuJoCo no __cplusplus surgery is needed here.

#define TRACY_ENABLE

#include "tracy/TracyC.h"
