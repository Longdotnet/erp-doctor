# ErpDoctor.PluginSdk

Public, dependency-light contracts for writing ERP Doctor diagnostic plugins.

Plugins run inside the ERP Doctor process with the same operating-system permissions as ERP Doctor. Only load plugin assemblies you trust, and never emit passwords, tokens, connection strings, or other secrets in diagnostic summaries/evidence.

The v1 API is intentionally small: implement `IErpDoctorPlugin`, return one or more `IPluginCheck` instances, and use the plugin-specific JSON configuration supplied through `PluginContext`.
