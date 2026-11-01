# Extension key and ID

The manifest `key` pins the extension ID so the native messaging host can whitelist it.
The private key was discarded; only the public key is needed.

- Extension ID: `depjhibopdmbilmmphmipdklabicghkf`
- Manifest `key`:

```
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAl5FsZR7w2ciqLX6kuhduPJlKi+7gkF0z7sXUFolQPYiG71m0orp6hMPI9VsvTv9hIo0gw6VH9avxlv6Z90qcRUL7KTRVEnm1/6MybHntTScgInx/FgvlTEDgt/9tWs9yZjtJWQrd2NJZCBuBvodJLbFSNBI9D4WaKnLiBJwUb2TehlZrILfyfazl9AAKQe5kfbIOsBMAZAxdunZse4cnFL1MReLSj87WLXWBLoyDfblaOTeJAEtWMlor0yjJ9/sWGIklBMSUoOiMe7N/Zc+Ex5SYfbwGx5Bve+qehfmBFDqPhaL0xFNNEBzzHHhuAZPYHKD3AhFS0t5GPip7AJwTvwIDAQAB
```

Register the host for your browsers: `bdm-host.exe --register depjhibopdmbilmmphmipdklabicghkf chrome edge brave`
