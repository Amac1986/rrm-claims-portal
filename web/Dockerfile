# syntax=docker/dockerfile:1
# Build context is web/, not the repo root.
#
# The final image is nginx-unprivileged: the master process runs as an unprivileged user,
# which is why it listens on 8080 rather than 80. It also processes
# /etc/nginx/templates/*.template with envsubst at startup, which is how API_UPSTREAM is
# injected without baking a hostname into the image.

ARG NODE_VERSION=22

# ---------- build ----------
FROM node:${NODE_VERSION}-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

# ---------- runtime ----------
FROM nginxinc/nginx-unprivileged:1.29-alpine AS final

ENV API_UPSTREAM=api:8080

COPY nginx.conf /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080
