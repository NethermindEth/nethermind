# FastRlp

Declarative RLP encoding a decoding with support for extensibility through manually written `IRlpConverter`s and automatically generated through attributes.

## TODO

- Avoid the need for closures through the usage of an extra "context" argument
- Add support more instances for base types
- Add support for generic Arrays (`X[]`)
- Alternative API for writing based on `Async` and `Stream`
- Support for parameterizable names when using attributes
