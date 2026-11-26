# PersonalControl — Contexto do Projeto & Roadmap

Este documento resume a arquitetura, o fluxo de deploy e as ideias futuras do **PersonalControl** (sem senhas ou segredos), para manter o contexto sincronizado entre computadores.

## Repositórios
- **Frontend (React + TypeScript + Vite):** `personal-finance`
- **Backend (ASP.NET Core .NET 11 + PostgreSQL / Npgsql):** `personal-finance-API`
- **Produção:** `https://personalcontrol.cloud` (VPS Linux com Docker Compose em `/app/personalcontrol`)

---

## Fluxo de Deploy (VPS leve — build local)
A VPS roda imagens leves (`aspnet:11.0-alpine` + `nginx:alpine`) usando os artefatos pré-compilados commitados no Git:

1. **Quando alterar a API (`personal-finance-API`):**
   ```powershell
   dotnet publish PersonalControl.Api.csproj -c Release -o publish
   Remove-Item -Force -ErrorAction SilentlyContinue publish/appsettings.Development.json
   git add -A
   git commit -m "mensagem"
   git push
   ```
2. **Quando alterar o Frontend (`personal-finance`):**
   ```powershell
   npm run build
   npm run lint
   git add -A
   git commit -m "mensagem"
   git push
   ```
3. **Na VPS (`/app/personalcontrol`):**
   ```bash
   bash atualizar.sh
   ```
   *(O script faz `git pull` nos dois repositórios e roda `docker compose up -d --build`. As migrações do PostgreSQL são aplicadas automaticamente na inicialização da API em `Data/PostgreSql/PostgresDatabase.cs`.)*

---

## O que já está implementado
- **Autenticação e sessões robustas (migrações v7/v8):**
  - Access token JWT curto (15 min) somente em memória no front (sem `localStorage`/`sessionStorage`).
  - Refresh token opaco (30 dias) em cookie `HttpOnly` (`pc_refresh`), salvo em hash SHA-256 na tabela `user_sessions`, com rotação atômica e detecção de reuso por família.
  - Sincronização de login/logout entre abas (`BroadcastChannel`), rate limiting no login/cadastro/refresh e painel de **Sessões ativas** em Cadastros.
  - **Admin & Allowlist:** somente e-mails autorizados no painel **Admin** (`allowed_emails`) podem criar novas contas. A primeira conta criada é promovida automaticamente a admin (`is_admin = TRUE`).
  - Hardening: `ASPNETCORE_ENVIRONMENT=Production` fixado no `Dockerfile`, `WildcardAccess` removido, headers HTTP de segurança e `X-Forwarded-Proto` preservado no `nginx.conf`.
- **Financeiro completo (100% gratuito / foco no controle manual rápido):**
  - Dashboard mensal, contas, categorias, orçamentos, cartões (faturas e parcelas).
  - **Botão flutuante `+ Lançar`** acessível de qualquer tela (valor focado, chips de categoria em 1 toque, lembra última conta/cartão, suporta Despesa, Receita e Cartão).
  - **Contas Fixas / Recorrentes** (migração v10): cadastro de receitas/despesas fixas mensais e lançamento em 1 clique no mês sem duplicar.
  - **Importação OFX e CSV** com auto-categorização baseada no histórico do próprio usuário.
  - **Metas & Streak Diário** (migração v9): limite de gasto diário com grade de quadradinhos (verde/vermelho), sequência atual/melhor sequência no mês, e metas de economia mensal ou caixinhas com aportes.
  - **UI Mobile:** favicon SVG da marca, barra inferior fixa de 5 abas (*Início*, *Extrato*, *Metas*, *Cartões*, *Cadastros*) e Open Finance (Pluggy, opcional com webhook + sync automática) movido para dentro de Cadastros.

---

## Ideias na caixinha (próximos passos futuros)
- **App Android 100% nativo (`personal-finance-android`):**
  - **Stack:** Kotlin + Jetpack Compose + Material 3 (mesmo tema dark roxo `#0b0a12` / `#a78bfa`) + **Jetpack Glance** para Widgets na tela inicial.
  - **Widgets planejados:**
    - Widget de **Streak Diário + Gasto de hoje** com botão `+` direto para lançamento rápido.
    - Widget de **Saldo / Resumo do mês**.
  - **Arquitetura:** OkHttp + Retrofit + OkHttp `Authenticator` (refresh automático), Android Keystore (`EncryptedSharedPreferences`) + Biometria (`BiometricPrompt`), Room (cache local offline-first) e WorkManager.
  - **Ajuste necessário na API quando iniciar o app Android:** suportar retorno/envio do `refreshToken` no JSON quando o cliente enviar header `X-Client-Type: mobile` em `/auth/login`, `/auth/refresh` e `/auth/logout` (mantendo cookie `HttpOnly` no site web).
- **Outras ideias opcionais:**
  - PWA instalável no site atual (`manifest.json` + Service Worker).
  - Edição de lançamentos já criados e filtros/busca no extrato.
