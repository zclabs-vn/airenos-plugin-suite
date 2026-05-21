FROM node:20-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci --omit=dev || npm install --omit=dev

FROM node:20-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY --from=deps /app/node_modules ./node_modules
COPY package.json ./
COPY src ./src
COPY public ./public

# Payloads are persisted here — mount a volume for durability across container restarts.
RUN mkdir -p /app/data
VOLUME ["/app/data"]

EXPOSE 5099
CMD ["node", "src/server.js"]
