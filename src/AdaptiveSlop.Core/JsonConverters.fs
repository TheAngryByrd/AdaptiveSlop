namespace AdaptiveSlop.Core

open System
open System.Text.Json
open System.Text.Json.Serialization

/// <summary>
/// The System.Text.Json entry point for changeable nodes. The four changeable
/// node types (<c>cval</c>, <c>cset</c>, <c>cmap</c>, <c>clist</c>) carry a
/// <c>JsonConverter</c> attribute pointing here, so a plain
/// <c>JsonSerializer.Serialize</c> / <c>JsonSerializer.Deserialize</c> call
/// round-trips a node with no options and no converter registration.
/// </summary>
/// <remarks>
/// The wire format is the bare payload: cval -&gt; scalar, cset/clist -&gt; array,
/// cmap -&gt; object. Deserialization rebuilds the node through its constructor,
/// so the node attaches to the deserializing thread's ambient graph and all
/// transient machinery (sinks, journal, post ring, scratch buffers) starts
/// fresh. The version counter is not persisted.
/// </remarks>
/// <example>
/// <code>
/// let v = CVal.create 10
/// let json = JsonSerializer.Serialize v
/// let v' = JsonSerializer.Deserialize&lt;cval&lt;int&gt;&gt; json
/// </code>
/// </example>
type ChangeableConverterFactory() =
    inherit JsonConverterFactory()

    // Node type full name -> converter type full name. This factory is defined
    // before the node types (compile order), so it matches them by name
    // instead of referencing them.
    static let kinds: (string * string)[] =
        [| "AdaptiveSlop.Core.ChangeableValue`1", "AdaptiveSlop.Core.ChangeableValueConverter`1"
           "AdaptiveSlop.Core.ChangeableSet`1", "AdaptiveSlop.Core.ChangeableSetConverter`1"
           "AdaptiveSlop.Core.ChangeableMap`2", "AdaptiveSlop.Core.ChangeableMapConverter`2"
           "AdaptiveSlop.Core.ChangeableList`1", "AdaptiveSlop.Core.ChangeableListConverter`1" |]

    override _.CanConvert(typeToConvert: Type) =
        typeToConvert.IsGenericType
        && Array.exists (fun (nodeName, _) -> nodeName = typeToConvert.GetGenericTypeDefinition().FullName) kinds

    override _.CreateConverter(typeToConvert: Type, _options: JsonSerializerOptions) =
        let def = typeToConvert.GetGenericTypeDefinition()

        let convName =
            kinds |> Array.find (fun (nodeName, _) -> nodeName = def.FullName) |> snd

        let convDef = typeof<ChangeableConverterFactory>.Assembly.GetType(convName)

        (convDef.MakeGenericType(typeToConvert.GetGenericArguments())
         |> Activator.CreateInstance
        :?> JsonConverter)
