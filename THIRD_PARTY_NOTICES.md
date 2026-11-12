# Third-party notices

NodeX's own source remains under the MIT license. Dependencies retain their own licenses.

## Desktop Bluetooth

The desktop build includes **SimpleSharpBLE 1.4.0 / SimpleBLE**, copyright The California Open Source Company LLC, under the **Business Source License 1.1** with its noncommercial additional-use grant. This is not an MIT or OSI open-source license. The grant covers personal/hobby projects and open-source projects that generate no revenue; commercial use requires the licensor's separate permission/license under the supplied terms.

The exact license from the NuGet package is in [`docs/licenses/SimpleBLE-LICENSE.md`](docs/licenses/SimpleBLE-LICENSE.md) and is copied to `licenses/SimpleBLE-LICENSE.md` in desktop build/publish output. Android/iOS keep their existing native BLE implementations and do not reference this package.

Upstream: [SimpleBLE](https://github.com/simpleble/simpleble), [SimpleSharpBLE usage and platform support](https://docs.simpleble.org/simplesharpble/usage). The pinned package ships native libraries for Windows/Linux x64 and ARM64, and Apple Silicon macOS 15+. Real PTT-button behavior has not been verified for NodeX.
