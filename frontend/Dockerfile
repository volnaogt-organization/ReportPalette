# Фронтенд ReportPalette: статика + nginx-прокси /api -> backend:8000
FROM nginx:alpine

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY index.html styles.css app.js sha256.js /usr/share/nginx/html/

EXPOSE 80
