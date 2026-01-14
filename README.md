# AdaptiveSlop

A high-performance, low-allocation incremental/adaptive computation library for F#.

Inspired by [FSharp.Data.Adaptive](https://github.com/fsprojects/FSharp.Data.Adaptive), AdaptiveSlop provides automatic dependency tracking and incremental recomputation with a focus on memory efficiency and performance.

## Features

- **Automatic dependency tracking** - No manual subscription management
- **Incremental recomputation** - Only recomputes what changed
- **Low memory allocation** - 14x less memory than FSharp.Data.Adaptive
- **Thread-safe** - Fully concurrent read/write support
- **Transaction support** - Batch updates atomically
- **Optimized N-ary operations** - 3-100x faster for wide fan-in patterns

## Installation

```bash
dotnet add package AdaptiveSlop.Core
```

## Quick Start

```fsharp
open AdaptiveSlop.Core

// Create changeable values (inputs)
let width = CVal.create 10.0
let height = CVal.create 20.0

// Create computed values (automatically track dependencies)
let area = AVal.map2 (*) (CVal.value width) (CVal.value height)

// Read the current value
printfn "Area: %f" (AVal.getValue area)  // Area: 200.0

// Change an input - the computed value updates automatically
width.Set(15.0)
printfn "Area: %f" (AVal.getValue area)  // Area: 300.0
```

## Core Concepts

### Changeable Values (`CVal`)

Changeable values are the **inputs** to your computation graph. They can be modified at any time.

```fsharp
// Create
let counter = CVal.create 0

// Read (get the IAdaptiveValue interface)
let counterValue = CVal.value counter

// Modify
counter.Set(42)
```

### Adaptive Values (`AVal`)

Adaptive values are **computed** from other adaptive values. They automatically track dependencies and recompute when inputs change.

```fsharp
// Transform a single value
let doubled = AVal.map (fun x -> x * 2) (CVal.value counter)

// Combine two values
let sum = AVal.map2 (+) (CVal.value a) (CVal.value b)

// Combine three values (optimized - no intermediate nodes)
let rgb = AVal.map3 (fun r g b -> (r, g, b)) 
                    (CVal.value red) (CVal.value green) (CVal.value blue)

// Combine four values (optimized - no intermediate nodes)
let rect = AVal.map4 (fun x y w h -> { X = x; Y = y; Width = w; Height = h })
                     (CVal.value x) (CVal.value y) (CVal.value width) (CVal.value height)

// Read the current computed value
let currentValue = AVal.getValue doubled
```

### Wide Fan-In Operations (`mapN`, `reduce`, `sum`)

When combining many values (5+), use specialized operations for dramatically better performance:

```fsharp
// Combine N values with a function
let sensors = Array.init 100 (fun i -> CVal.create (float i))
let deps = sensors |> Array.map (fun s -> CVal.value s :> IAdaptiveValue<float>)

let average = AVal.mapN (fun values -> Array.average values) deps
let total = AVal.reduce 0.0 (+) deps

// Convenience function for summing integers
let intSensors = Array.init 100 (fun i -> CVal.create i)
let intDeps = intSensors |> Array.map (fun s -> CVal.value s :> IAdaptiveValue<int>)
let totalInt = AVal.sum intDeps
```

**Performance comparison** (100 inputs, 100 iterations):

| Method | Time | vs FDA |
|--------|------|--------|
| map2 chain | 789 us | 1.10x slower |
| **reduce** | **233 us** | **3.1x faster** |
| FSharp.Data.Adaptive | 714 us | baseline |

### Transactions

Batch multiple changes into a single atomic update:

```fsharp
Transaction.run (fun () ->
    width.Set(100.0)
    height.Set(50.0)
    // Computed values won't update until transaction commits
)
// Now all computed values reflect both changes atomically
```

### Adaptive Collections

AdaptiveSlop also supports adaptive sets and maps:

```fsharp
// Adaptive Set
let items = CSet.ofSeq [1; 2; 3]
let doubled = ASet.map (fun x -> x * 2) (CSet.value items)
let filtered = ASet.filter (fun x -> x > 2) (CSet.value items)

items.Add(4)
items.Remove(1)

// Adaptive Map
let scores = CMap.ofSeq [("Alice", 95); ("Bob", 87)]
let curved = AMap.map (fun _ score -> score + 5) (CMap.value scores)

scores.AddOrUpdate("Charlie", 92)
```

## API Reference

### AVal Module

| Function | Description |
|----------|-------------|
| `constant value` | Creates a constant adaptive value that never changes |
| `map f value` | Transforms an adaptive value |
| `map2 f left right` | Combines two adaptive values |
| `map3 f a b c` | Combines three adaptive values (optimized) |
| `map4 f a b c d` | Combines four adaptive values (optimized) |
| `mapN compute deps` | Combines N adaptive values (optimized for wide fan-in) |
| `reduce init op deps` | Reduces N adaptive values with a binary operation |
| `sum deps` | Sums N adaptive integer values |
| `bind f value` | Monadic bind (dynamic dependency) |
| `getValue value` | Gets the current computed value |

### CVal Module

| Function | Description |
|----------|-------------|
| `create value` | Creates a new changeable value |
| `value cval` | Gets the IAdaptiveValue interface |
| `set value cval` | Sets a new value |

### CSet Module

| Function | Description |
|----------|-------------|
| `empty` | Creates an empty changeable set |
| `ofSeq items` | Creates a changeable set from a sequence |
| `add item set` | Adds an item |
| `remove item set` | Removes an item |
| `set value cset` | Replaces all items |

### CMap Module

| Function | Description |
|----------|-------------|
| `empty` | Creates an empty changeable map |
| `ofSeq pairs` | Creates a changeable map from key-value pairs |
| `addOrUpdate key value map` | Adds or updates a key |
| `remove key map` | Removes a key |
| `set value cmap` | Replaces all entries |

## Performance Guidance

### When to Use Each Function

| Scenario | Recommended Function |
|----------|---------------------|
| 1 dependency | `map` |
| 2 dependencies | `map2` |
| 3 dependencies | `map3` |
| 4 dependencies | `map4` |
| 5+ dependencies (same type) | `mapN` or `reduce` |
| Sum of integers | `sum` |
| Deep dependency chains | Standard functions work great |
| Wide fan-in (many inputs to one output) | `mapN`, `reduce`, or `sum` |

### Performance vs FSharp.Data.Adaptive

| Scenario | AdaptiveSlop | Memory |
|----------|--------------|--------|
| Deep chains (depth 20) | 1.2x faster | 38% less |
| Wide fan-in (100 inputs) | 3.1x faster | 14.7x less |
| Wide fan-in (500 inputs) | 3.3x faster | 14.7x less |

## Thread Safety

All operations in AdaptiveSlop are thread-safe:

- Multiple threads can read adaptive values concurrently
- Multiple threads can modify changeable values concurrently
- Reads always see a consistent snapshot
- Transactions are thread-local (changes in a transaction on one thread are isolated until commit)

## Architecture

AdaptiveSlop uses a **hybrid pull/push model**:

1. **Pull (on read)**: When you call `GetValue()`, the system checks if dependencies changed and recomputes if needed
2. **Push (invalidation)**: When a source changes, it marks dependent nodes as "dirty" for efficient change detection

This hybrid approach provides:
- Lazy evaluation (don't compute until needed)
- Efficient change detection (don't scan unchanged branches)
- No memory leaks from forgotten subscriptions

## Examples

### Temperature Converter

```fsharp
let celsius = CVal.create 20.0

let fahrenheit = AVal.map (fun c -> c * 9.0/5.0 + 32.0) (CVal.value celsius)
let kelvin = AVal.map (fun c -> c + 273.15) (CVal.value celsius)

printfn "%.1f C = %.1f F = %.1f K" 
    (AVal.getValue (CVal.value celsius))
    (AVal.getValue fahrenheit)
    (AVal.getValue kelvin)

celsius.Set(100.0)
// All derived values update automatically
```

### Shopping Cart

```fsharp
let items = CMap.ofSeq [("apple", 1.50); ("bread", 2.00)]
let quantities = CMap.ofSeq [("apple", 3); ("bread", 1)]

let itemTotals = 
    AVal.map2 (fun prices qtys ->
        prices |> Map.map (fun item price ->
            match Map.tryFind item qtys with
            | Some qty -> price * float qty
            | None -> 0.0))
        (CMap.value items)
        (CMap.value quantities)

let grandTotal = 
    AVal.map (fun totals -> totals |> Map.toSeq |> Seq.sumBy snd) itemTotals

items.AddOrUpdate("milk", 3.50)
quantities.AddOrUpdate("milk", 2)
// grandTotal automatically updates
```

### Sensor Aggregation

```fsharp
// 100 temperature sensors
let sensors = Array.init 100 (fun i -> CVal.create (20.0 + float i * 0.1))
let deps = sensors |> Array.map (fun s -> CVal.value s :> IAdaptiveValue<float>)

// Efficient aggregation using reduce
let avgTemp = AVal.mapN (fun temps -> Array.average temps) deps
let maxTemp = AVal.reduce System.Double.MinValue max deps
let minTemp = AVal.reduce System.Double.MaxValue min deps

// Update a sensor
sensors.[50].Set(25.0)
// All aggregations update efficiently
```

## License

MIT

## Contributing

Contributions are welcome! Please open an issue to discuss proposed changes.
