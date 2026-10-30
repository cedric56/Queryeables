# Diagnostics QRY001-QRY009

The generator reports mistakes in `[EntityCache]` and `[EntityGraph]` as compile errors, on the attribute.

| Id | Problem | Fix |
| --- | --- | --- |
| QRY001 | The entity has no key | Add an `Id` property, or mark the key with `[System.ComponentModel.DataAnnotations.Key]` |
| QRY002 | `[EntityGraph]` on a property that is not a writable collection of entities | Use `List<T>`, `IList<T>`, `ICollection<T>`... with a setter. A single reference is not a graph: remove the attribute |
| QRY003 | The children's foreign key was not found | Name it `{Parent}Id` or `Id{Parent}`, or set `ForeignKey` |
| QRY004 | A property named in the attribute does not exist | Check the `nameof(...)` |
| QRY005 | The foreign key type does not match the parent key | Same type, or its nullable form |
| QRY006 | The key of the looked-up entity was not found | Give it an `Id` or a `[Key]` |
| QRY007 | The lookup foreign key type does not match the looked-up key | Same type, or its nullable form |
| QRY008 | `ParentNavigation` cannot hold the parent | It must be of the parent type. To point to another entity, use `LookupNavigation` |
| QRY009 | `LookupForeignKey` is the child's own key | Name the property that holds the other entity's key |
