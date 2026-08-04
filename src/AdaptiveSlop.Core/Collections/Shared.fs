namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic

// =============================================================================
// Constant adaptive collections

// =============================================================================
// Shared collection operations (Section 6.9 of PLAN.md)
//
// Inline [<InlineIfLambda>] passes: the per-node lambda (mapping, predicate,
// identity) is inlined into the shared pass by the compiler.
// Byref operations: the node state lives in structs so the shared operations
// can address it without abstract classes or virtual dispatch.
// =============================================================================

module internal Collections =
    ()
