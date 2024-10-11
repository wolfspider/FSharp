# FSharp
.Net Core FSharp Http Server based on miTLS

I'm using this repo to learn F# and FP.

So what can I say after all this time so many things have changed? I decided it was worth actually making this work during the
hurricane milton.

This project has been updated to .Net Core 8

After getting the fibers to actually work I realized this web server's best traits are consistent and reliable
performance with a slim number of dependencies. TLS has been ripped out and do not think I will get back to
that. This is techinically how an old fashion web server performs line by line. It is great for debugging clients.
Slamming this server should not slam the machine and performance could theoretically scale with the number schedulers.

Fibers ZIO experiment code was written by Bartosz Sypytkowski [https://bartoszsypytkowski.com/building-custom-fibers-library-in-f/]

I would like to point out that I in no way condone any of the fallout which occured with any aformentioned projects nor their
views. People are people, I cannot control them. This was developed before any of the lurid details spilled out into the
open obviously. 

miTLS original web server for miTLS-Flex [https://github.com/mitls/mitls-flex/tree/master/apps/HttpServer]

CoreRT experimental AOT runtime [https://github.com/dotnet/corert]

AOT has finally become a thing and this project has also transitioned to Native AOT deployment [https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot]

This now compiles in AOT mode with warnings so the project has trimming enabled. I am not sure what that is about but glad it actually builds and runs. Currently this produces a standalone binary ~5MB.

I've also decided to introduce Clojure's Atoms. So basically, since fibers currently work within the context of Interlocked.Exchange a Fiber can be thought of as an atomic coroutine. Atoms are Fibers and Fibers are Atoms in that sense.

The Closure implmentation can be found here: [https://www.fssnip.net/1V/title/Clojures-Atoms]

Not to be used in production this is an experiment....still
