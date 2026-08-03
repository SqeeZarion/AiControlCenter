FROM node:24.18.1-alpine AS build
WORKDIR /app
COPY frontend/ai-control-center-angular/package*.json ./
RUN npm ci
COPY frontend/ai-control-center-angular/ ./
RUN npm run build -- --configuration production

FROM nginx:1.28.0-alpine AS final
COPY frontend/ai-control-center-angular/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/ai-control-center-angular/browser /usr/share/nginx/html
EXPOSE 80
