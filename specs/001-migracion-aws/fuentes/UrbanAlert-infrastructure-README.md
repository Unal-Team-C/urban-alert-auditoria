# Infraestructura local

`docker-compose.yml` levanta todos los servicios de UrbanAlert con sus bases de datos y RabbitMQ, detrás de un **gateway (Nginx)**. El gateway es el único punto de entrada.

## Uso

Desde esta carpeta:

```bash
docker compose up -d --build     # construye las imágenes y levanta todo
docker compose ps                # estado de los servicios
docker compose logs -f reportes  # logs de un servicio
docker compose down              # detiene todo (conserva los datos)
docker compose down -v           # detiene todo y borra los volúmenes
```

Abrir **http://localhost:8080**.

Las credenciales tienen valores por defecto **solo para desarrollo**. Para cambiarlas: `cp .env.example .env` y editar `.env` (no se versiona).

## Gateway: rutas públicas

| Ruta | Destino interno | Notas |
|---|---|---|
| `/` | `frontend:3000` | Formulario de reportes |
| `/api/v1/reportes/*` | `reportes:8080/api/v1/Reportes/*` | CRUD y catálogo (`/api/v1/reportes/catalogo`) |
| `/api/v1/geoespacial/reports` | `geoespacial-api:8000/api/v1/geospatial/reports` | **Solo GET** (mapa). La asignación de coordenadas es interna |
| `/api/v1/geoespacial/coordinates/{id}` | `geoespacial-api:8000/api/v1/geospatial/coordinates/{id}` | **Solo GET**: consulta una coordenada por su id (`idCoordenada` del reporte) |
| `/api/v1/usuarios/*` | `usuarios:8080/api/v1/users/*` | |
| `/api/v1/auditoria/*` | `auditoria:8080/auditoria/*` | El gateway agrega `X-Urban-Gateway-Key`; sin ella Auditoria responde 403 |
| `/health` | — | Salud del gateway |

Cualquier otra ruta bajo `/api/` responde 404. La configuración está en `gateway/default.conf.template`.

### Documentación de las APIs

Índice en `/docs/` (por ejemplo http://localhost:8080/docs/ o la URL del túnel + `/docs/`):

| Ruta | Documentación |
|---|---|
| `/docs/reportes` | Scalar de Reportes |
| `/docs/auditoria` | Scalar de Auditoria |
| `/docs/usuarios` | Swagger UI de Usuarios |
| `/docs/geoespacial` | Swagger UI de Geoespacial |

El gateway quita el prefijo `/docs/<servicio>` y reescribe las URLs absolutas que generan los servicios, así que "probar" desde la documentación funciona y llega a las mismas rutas que ya publica `/api/v1/*` (en Geoespacial, solo `GET /reports` y `GET /coordinates/{id}`). Scalar de Reportes y Auditoria solo existe con `ASPNETCORE_ENVIRONMENT=Development`, que es lo que usa este compose.

## Aislamiento

Solo el gateway publica un puerto en el host (`GATEWAY_PORT`, por defecto 8080). Redes de Docker:

| Red | Quiénes | Para qué |
|---|---|---|
| `edge` | gateway, frontend | El frontend solo ve al gateway |
| `backend` | gateway, APIs, RabbitMQ | Rutas del gateway y comunicación entre servicios |
| `data-reportes` | postgres-reportes, reportes, auditoria | Cada API alcanza solo su base de datos |
| `data-geoespacial` | geoespacial-db, geoespacial-api | |
| `data-usuarios` | postgres-usuarios, usuarios | |

```
                 localhost:8080
                       │
                   gateway ─────────────── frontend            (edge)
                       │
     ┌─────────┬───────┴──────┬────────────────┐
  reportes  auditoria   geoespacial-api     usuarios           (backend, con rabbitmq)
     │  └── HTTP ──────────►  │                  │
     │  ReporteCreado ► rabbitmq ► auditoria     │
     ▼                        ▼                  ▼
 postgres-reportes      geoespacial-db    postgres-usuarios    (data-*)
```

## Modo debug: puertos de cada servicio

Para usar Scalar o Swagger, o para conectarse a las bases de datos o a RabbitMQ:

```bash
docker compose -f docker-compose.yml -f docker-compose.debug.yml up -d
```

| Servicio | Puerto | Documentación |
|---|---|---|
| frontend | 3000 | |
| reportes | 5039 | http://localhost:5039/scalar/v1 |
| auditoria | 5040 | http://localhost:5040/scalar/v1 |
| geoespacial-api | 8000 | http://localhost:8000/docs |
| usuarios | 8081 | http://localhost:8081/swagger-ui.html |
| rabbitmq | 5672, 15672 | Panel: http://localhost:15672 |
| postgres-reportes / geoespacial-db / postgres-usuarios | 5432 / 5433 / 5434 | |

## Publicar con Cloudflare Tunnel (máquina remota)

El túnel apunta al gateway por la red interna de Docker. La máquina **no necesita abrir puertos de entrada** ni tener IP pública, y Cloudflare pone el HTTPS.

> ⚠️ **La API no tiene autenticación.** Quien tenga la URL puede crear y borrar reportes y usuarios. Para algo más que una demo puntual, usar el túnel con nombre **protegido con Cloudflare Access**.

### Opción A: Quick Tunnel (demo rápida, sin cuenta)

```bash
docker compose --profile tunnel-rapido up -d --build
docker compose logs tunnel-rapido | grep -o 'https://.*trycloudflare.com'
```

La URL es aleatoria y cambia cada vez que se reinicia el contenedor. No se puede proteger con Access.

### Opción B: túnel con nombre (URL fija y acceso controlado)

Requiere una cuenta de Cloudflare y un dominio administrado en ella.

1. En Cloudflare → **Zero Trust → Networks → Tunnels**, crear un túnel de tipo *Cloudflared* y copiar su **token**.
2. En el túnel, agregar un **Public Hostname**, por ejemplo `urbanalert.tudominio.com`, con servicio `HTTP` y URL `gateway:80`.
3. **Proteger el hostname**: en **Zero Trust → Access → Applications**, crear una aplicación *Self-hosted* para ese hostname, con una política *Allow* que liste los correos del equipo. Cloudflare pedirá un código por correo antes de dejar entrar. Es gratis hasta 50 usuarios.
4. En la máquina remota:

   ```bash
   cp .env.example .env    # definir CLOUDFLARE_TUNNEL_TOKEN y contraseñas propias
   docker compose --profile tunnel up -d --build
   docker compose logs -f tunnel   # debe mostrar "Registered tunnel connection"
   ```

### Recomendaciones para la máquina remota

- **Recursos**: al menos 4 GB de RAM y 2 vCPU. Construir las imágenes allí tarda varios minutos.
- **Credenciales**: definir contraseñas propias en `.env`, no usar los valores por defecto.
- **Puerto del gateway**: el túnel no lo necesita. Para no exponerlo en la red de la máquina: `GATEWAY_PORT=127.0.0.1:8080`.
- **Modo debug**: **no** combinar `docker-compose.debug.yml` con el túnel. Para revisar bases de datos o RabbitMQ en la máquina remota, usar un túnel SSH.

## Detalles

- **Reportes y Auditoria** corren con `ASPNETCORE_ENVIRONMENT=Development`. Así:
  - Reportes aplica sus migraciones al arrancar y expone Scalar;
  - Auditoria arranca sin Cognito. Sus endpoints siguen pidiendo un JWT, así que sin token responden 401, aunque la petición pase por el gateway. Con `AUDITORIA_MODO_DESARROLLO=true` en el `.env` aceptan la identidad en los headers `X-Usuario-Id` y `X-Usuario-Rol` (por defecto, admin); ver el README de Auditoria. Cualquiera que llegue al gateway puede elegir su identidad.
- **`postgres-reportes/01-auditoria.sh`** se ejecuta solo con el volumen vacío:
  - crea la base de Auditoria y los roles `audit_writer`, `audit_reader` y `reportes_reader`;
  - aplica `backend/UrbanAlert.Auditoria/database/001_audit_store.sql`;
  - da a `reportes_reader` permiso de lectura sobre las tablas que creen después las migraciones de Reportes.

  Para volver a ejecutarlo: `docker compose down -v`.
- **El frontend** llama a `/api/v1/reportes` y `/api/v1/usuarios` en su mismo origen. Con el compose, esas rutas las atiende el gateway. Con `npm run dev`, sin gateway, las atienden *rewrites* de Next.js hacia `REPORTES_API_URL` (por defecto `http://localhost:5039`) y `USUARIOS_API_URL` (por defecto `http://localhost:8081`).
- **En AWS**, el equivalente sería API Gateway con el *authorizer* de Cognito. Este Nginx solo reproduce las rutas y el header hacia Auditoria.
- Cada servicio conserva su propio compose (`backend/*/deploy/docker` o `compose.yaml`) para trabajar con él de forma aislada.
