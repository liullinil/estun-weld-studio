FROM node:22-bookworm-slim AS webdeps
WORKDIR /deps
COPY web/package.json web/package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates unzip python3 python3-venv libx11-6 libxcursor1 libxinerama1 libxrandr2 libxi6 libgl1 libfontconfig1 libglib2.0-0 && rm -rf /var/lib/apt/lists/*
COPY --from=node:22-bookworm-slim /usr/local/bin/node /usr/local/bin/node
RUN curl -fsSL https://github.com/godotengine/godot/releases/download/4.5.1-stable/Godot_v4.5.1-stable_mono_linux_x86_64.zip -o /tmp/godot.zip && unzip -q /tmp/godot.zip -d /opt/godot && rm /tmp/godot.zip && ln -s /opt/godot/Godot_v4.5.1-stable_mono_linux_x86_64/Godot_v4.5.1-stable_mono_linux.x86_64 /usr/local/bin/godot
RUN python3 -m venv /opt/cad && /opt/cad/bin/pip install --no-cache-dir cadquery-ocp==7.7.2.2b2 vtk==9.2.6
WORKDIR /app/native
COPY native/ ./
RUN dotnet build EstunStudio.csproj --nologo -v minimal
RUN godot --headless --editor --path /app/native --import --quit
WORKDIR /app/web
COPY web/ ./
COPY --from=webdeps /deps/node_modules ./node_modules
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh && useradd --create-home --uid 10001 estun && chown -R estun:estun /app
ENV HOST=0.0.0.0 PORT=18740 CAD_PYTHON=/opt/cad/bin/python CAD_WORKER=/app/native/tools/cad_import.py DOTNET_CLI_TELEMETRY_OPTOUT=1 GODOT_SILENCE_ROOT_WARNING=1
USER estun
EXPOSE 18740
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s CMD curl -fsS http://127.0.0.1:18740/api/health || exit 1
ENTRYPOINT ["/app/entrypoint.sh"]
