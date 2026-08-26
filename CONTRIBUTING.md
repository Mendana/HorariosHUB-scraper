# Contribuir

Proyecto pequeño (2 personas), así que el flujo es simple.

## Ramas

- `main` es la rama estable/desplegada. No se hacen commits directos.
- `dev` es la rama de integración. Todo el trabajo nuevo sale de `dev` y
  vuelve a `dev` mediante PR.
- `dev` se mergea a `main` cuando hay una versión lista para desplegar.

## Flujo

1. Crea una rama a partir de `dev` (`feat-x`, `fix-x`, ...).
2. Abre la PR contra `dev` (no contra `main`).
3. Pide revisión a la otra persona (ver [CODEOWNERS](.github/CODEOWNERS)).
4. Prueba los cambios localmente antes de pedir revisión.

## Issues

Usa las plantillas de bug/feature al crear un issue y etiqueta el área
afectada (`area:scraper`, `area:api`, `area:frontend`, `area:infra`).

## Más contexto

La documentación más detallada (arquitectura, despliegue, decisiones de
diseño) vive en la wiki del repo de backend.
