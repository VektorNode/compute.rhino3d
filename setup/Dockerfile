FROM ubuntu:noble

# Avoid interactive prompts
ENV DEBIAN_FRONTEND=noninteractive

# ============================================================
# 1. Install base dependencies
# ============================================================
# fontconfig is required by start.sh, which runs fc-cache over the custom fonts
# mounted at /usr/local/share/fonts/custom. libgdiplus backs System.Drawing.Common
# (see the symlink step below). Base font packages are no longer listed here:
# rhino3d >= 9.0.26258 depends on fonts-liberation and ships its own fonts under
# /usr/lib/rhino3d/Fonts, so apt pulls them in with the runtime.
RUN apt update && apt install -y \
    wget gpg nano git curl ca-certificates jq \
    libfontconfig1 fontconfig \
    libgdiplus \
    && rm -rf /var/lib/apt/lists/*

# ============================================================
# 2. Install .NET 10 SDK
# ============================================================
RUN wget https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh \
      -O /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh

ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="${PATH}:${DOTNET_ROOT}"

# System.Drawing.Common looks for libgdiplus by the names libgdiplus.dll /
# libgdiplus.dll.so, but the apt package ships it as libgdiplus.so.0. Resolve the
# real path from the package's own file list and alias the names the loader wants.
RUN GDIP="$(dpkg -L libgdiplus | grep -m1 'libgdiplus\.so')" \
    && ln -sf "$GDIP" /usr/lib/libgdiplus.so \
    && ln -sf "$GDIP" /usr/lib/libgdiplus.dll \
    && ln -sf "$GDIP" /usr/lib/libgdiplus.dll.so

# ============================================================
# 3. Add McNeel package repo and install Rhino runtime libs
# ============================================================
# RHINO3D_VERSION pins the daily WIP build of the rhino3d core libraries. McNeel
# publishes a new one most nights and `stable main` always resolves to the newest,
# so an unpinned build is not reproducible: a FRESH=1 rebuild silently moves to
# whatever shipped overnight. 9.0.26261.7009 regressed Grasshopper plugin loading
# on Linux (System.Drawing.Common failures in GH priority objects / .ghuser files,
# followed by a native LINUXFAIL abort in RhinoCore on the first solve), so we hold
# at the last known-good build. Set to empty to track latest again.
ARG RHINO3D_VERSION=9.0.26260.7019

RUN wget -qO- https://mcneel-packages.s3.amazonaws.com/mcneel-packages.gpg.key \
      | gpg --dearmor -o /usr/share/keyrings/mcneel-archive-keyring.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/mcneel-archive-keyring.gpg] \
      https://mcneel-packages.s3.amazonaws.com/deb stable main" \
      | tee /etc/apt/sources.list.d/mcneel.list \
    && apt update \
    # rhino3d arrives as a dependency of rhino-compute, so install the pinned
    # version first and hold it, otherwise apt resolves the dependency to latest.
    && if [ -n "${RHINO3D_VERSION}" ]; then \
         apt install -y "rhino3d=${RHINO3D_VERSION}" \
         && apt-mark hold rhino3d; \
       fi \
    && apt install -y rhino-compute yak-cli \
    && rm -rf /var/lib/apt/lists/*

# ============================================================
# 4. Fix NuGet config (prevent empty file errors)
# ============================================================
RUN mkdir -p /root/.nuget/NuGet \
    && printf '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />\n  </packageSources>\n</configuration>\n' > /root/.nuget/NuGet/NuGet.Config

# ============================================================
# 5. Clone repo and checkout branch
#    Override with --build-arg REPO_URL=... and BRANCH=...
# ============================================================
ARG REPO_URL=https://github.com/VektorNode/compute.rhino3d.git
ARG BRANCH=9.x.selva

RUN git clone ${REPO_URL} /home/rhino-compute-src \
    && cd /home/rhino-compute-src \
    && git checkout ${BRANCH}

# ============================================================
# 6. Build
# ============================================================
WORKDIR /home/rhino-compute-src/src
RUN dotnet restore compute.sln \
    && dotnet build compute.sln -c Release

# ============================================================
# 7. Expose ports
#    6500 = main rhino.compute server (accessible from host)
#    6001 = child compute.geometry process (internal)
# ============================================================
EXPOSE 6500
EXPOSE 6001

# ============================================================
# 8. Startup script
# ============================================================
COPY start.sh /start.sh
RUN sed -i 's/\r$//' /start.sh && chmod +x /start.sh

CMD ["/start.sh"]