{{- define "denoise.name" -}}{{ .Chart.Name }}{{- end -}}
{{- define "denoise.fullname" -}}{{ printf "%s" .Release.Name | trunc 63 | trimSuffix "-" }}{{- end -}}
{{- define "denoise.tag" -}}{{ .Values.image.tag | default .Chart.AppVersion }}{{- end -}}
{{- define "denoise.labels" -}}
app.kubernetes.io/name: {{ include "denoise.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ include "denoise.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}
{{- define "denoise.selectorLabels" -}}
app.kubernetes.io/name: {{ include "denoise.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{/* Environment shared by every host container */}}
{{- define "denoise.env" -}}
- name: ConnectionStrings__DeNoise
  valueFrom:
    secretKeyRef:
      name: {{ required "postgres.connectionSecret is required" .Values.postgres.connectionSecret }}
      key: connectionString
- name: Otel__Endpoint
  value: {{ .Values.otel.endpoint | quote }}
- name: Health__OutboxLagThreshold
  value: {{ .Values.health.outboxLagThreshold | quote }}
- name: DeNoise__PublicBaseUrl
  value: {{ .Values.publicBaseUrl | quote }}
- name: DeNoise__IngestPublicBaseUrl
  value: {{ .Values.ingestPublicBaseUrl | quote }}
- name: DeNoise__ExternalDeadmanUrl
  value: {{ .Values.externalDeadmanUrl | quote }}
- name: Limits__PayloadBytes
  value: {{ .Values.limits.payloadBytes | quote }}
- name: Limits__IngestRps
  value: {{ .Values.limits.ingestRps | quote }}
- name: Limits__ApiPerMinutePerUser
  value: {{ .Values.limits.apiPerMinutePerUser | quote }}
- name: Limits__LoginPerMinutePerIp
  value: {{ .Values.limits.loginPerMinutePerIp | quote }}
- name: Retention__RawDays
  value: {{ .Values.retention.rawDays | quote }}
- name: Retention__NormalisedClosedDays
  value: {{ .Values.retention.normalisedClosedDays | quote }}
- name: Retention__EpisodeClosedMonths
  value: {{ .Values.retention.episodeClosedMonths | quote }}
- name: Retention__DeliveryAttemptDays
  value: {{ .Values.retention.deliveryAttemptDays | quote }}
- name: Retention__AuditMonths
  value: {{ .Values.retention.auditMonths | quote }}
- name: Retention__SessionExpiredDays
  value: {{ .Values.retention.sessionExpiredDays | quote }}
- name: Retention__LoginAttemptDays
  value: {{ .Values.retention.loginAttemptDays | quote }}
- name: Retention__BatchSize
  value: {{ .Values.retention.batchSize | quote }}
- name: Retention__RunAtUtcHour
  value: {{ .Values.retention.runAtUtcHour | quote }}
- name: Notifications__AllowInsecureDestinations
  value: {{ .Values.allowInsecureDestinations | quote }}
{{- /* Auth:Local (LocalAuthOptions, src/DeNoise.Application/Auth/PasswordPolicy.cs) */}}
- name: Auth__Local__PasswordMinLength
  value: {{ .Values.auth.local.passwordMinLength | quote }}
- name: Auth__Local__LockoutFailures
  value: {{ .Values.auth.local.lockoutFailures | quote }}
- name: Auth__Local__LockoutWindow
  value: {{ .Values.auth.local.lockoutWindow | quote }}
- name: Auth__Local__SelfServiceResetEnabled
  value: {{ .Values.auth.local.selfServiceResetEnabled | quote }}
{{- with .Values.auth.local.bootstrapPasswordSecret }}
- name: Auth__Local__BootstrapPassword
  valueFrom:
    secretKeyRef:
      name: {{ . }}
      key: password
{{- end }}
{{- /* Smtp (SmtpOptions, src/DeNoise.Infrastructure/Notifications/SmtpEmailChannel.cs); without a host the e-mail channel stays off */}}
{{- if .Values.smtp.host }}
- name: Smtp__Host
  value: {{ .Values.smtp.host | quote }}
- name: Smtp__Port
  value: {{ .Values.smtp.port | quote }}
- name: Smtp__From
  value: {{ .Values.smtp.from | quote }}
- name: Smtp__Security
  value: {{ .Values.smtp.security | quote }}
- name: Smtp__MessageIdDomain
  value: {{ .Values.smtp.messageIdDomain | quote }}
{{- if .Values.smtp.username }}
- name: Smtp__Username
  value: {{ .Values.smtp.username | quote }}
{{- else if .Values.smtp.credentialsSecret }}
- name: Smtp__Username
  valueFrom:
    secretKeyRef:
      name: {{ .Values.smtp.credentialsSecret }}
      key: username
      optional: true
{{- end }}
{{- with .Values.smtp.credentialsSecret }}
- name: Smtp__Password
  valueFrom:
    secretKeyRef:
      name: {{ . }}
      key: password
      optional: true
{{- end }}
{{- end }}
{{- end -}}
{{/* Data Protection key ring claim (ADR-18): the configured name, the chart-created default, or empty (emptyDir fallback) */}}
{{- define "denoise.dataProtection.claimName" -}}
{{- if .Values.dataProtection.persistentVolumeClaim -}}
{{ .Values.dataProtection.persistentVolumeClaim }}
{{- else if .Values.dataProtection.createPvc -}}
{{ include "denoise.fullname" . }}-dataprotection-keys
{{- end -}}
{{- end -}}
{{/* Deployment body shared by the three hosts. Context: dict "root" . "host" "api" "spec" .Values.api */}}
{{- define "denoise.deployment" -}}
{{- $root := .root -}}{{- $host := .host -}}{{- $spec := .spec -}}
{{- $keysSecret := $root.Values.dataProtection.keysSecret -}}
{{- $keysClaim := include "denoise.dataProtection.claimName" $root -}}
{{- if and $keysSecret $keysClaim -}}
{{- fail "dataProtection.keysSecret and dataProtection.persistentVolumeClaim / createPvc are alternatives; set one" -}}
{{- end -}}
{{- if and $keysSecret (not $root.Values.dataProtection.keysSecretKeys) -}}
{{- fail "dataProtection.keysSecretKeys must list at least one key of dataProtection.keysSecret" -}}
{{- end -}}
{{- /* A key ring from the Secret is read-only and needs no directory: keysPath (and its volume) only apply without it */}}
{{- $keysPath := ternary "" $root.Values.dataProtection.keysPath (not (empty $keysSecret)) -}}
{{- if and $keysClaim (not $keysPath) -}}
{{- fail "dataProtection.keysPath is required when dataProtection.persistentVolumeClaim or dataProtection.createPvc is set" -}}
{{- end -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ include "denoise.fullname" $root }}-{{ $host }}
  labels:
    {{- include "denoise.labels" $root | nindent 4 }}
    app.kubernetes.io/component: {{ $host }}
spec:
  replicas: {{ $spec.replicas }}
  selector:
    matchLabels:
      {{- include "denoise.selectorLabels" $root | nindent 6 }}
      app.kubernetes.io/component: {{ $host }}
  template:
    metadata:
      labels:
        {{- include "denoise.selectorLabels" $root | nindent 8 }}
        app.kubernetes.io/component: {{ $host }}
    spec:
      {{- with $root.Values.image.pullSecrets }}
      imagePullSecrets: {{- toYaml . | nindent 8 }}
      {{- end }}
      securityContext:
        runAsNonRoot: true
        seccompProfile: { type: RuntimeDefault }
      affinity:
        podAntiAffinity:
          preferredDuringSchedulingIgnoredDuringExecution:
            - weight: 100
              podAffinityTerm:
                topologyKey: topology.kubernetes.io/zone
                labelSelector:
                  matchLabels:
                    {{- include "denoise.selectorLabels" $root | nindent 20 }}
                    app.kubernetes.io/component: {{ $host }}
      containers:
        - name: {{ $host }}
          image: "{{ $root.Values.image.repository }}-{{ $host }}:{{ include "denoise.tag" $root }}"
          imagePullPolicy: {{ $root.Values.image.pullPolicy }}
          {{- if eq $host "workers" }}
          args: ["--roles={{ $root.Values.workers.roles }}"]
          {{- end }}
          ports:
            - name: http
              containerPort: 8080
          env:
            {{- include "denoise.env" $root | nindent 12 }}
            {{- range $i, $key := ternary $root.Values.dataProtection.keysSecretKeys (list) (not (empty $keysSecret)) }}
            - name: DataProtection__Keys__{{ $i }}
              valueFrom:
                secretKeyRef:
                  name: {{ $keysSecret }}
                  key: {{ $key }}
            {{- end }}
            {{- with $keysPath }}
            - name: DataProtection__KeysPath
              value: {{ . | quote }}
            {{- end }}
          startupProbe:
            httpGet: { path: /healthz/startup, port: http }
            periodSeconds: 5
            failureThreshold: 36
          readinessProbe:
            httpGet: { path: /healthz/ready, port: http }
            periodSeconds: 10
          livenessProbe:
            httpGet: { path: /healthz/live, port: http }
            periodSeconds: 15
          resources: {{- toYaml $spec.resources | nindent 12 }}
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          volumeMounts:
            - { name: tmp, mountPath: /tmp }
            {{- with $keysPath }}
            - { name: dataprotection-keys, mountPath: {{ . | quote }} }
            {{- end }}
      volumes:
        - name: tmp
          emptyDir: {}
        {{- if $keysPath }}
        - name: dataprotection-keys
          {{- if $keysClaim }}
          persistentVolumeClaim:
            claimName: {{ $keysClaim }}
          {{- else }}
          emptyDir: {}
          {{- end }}
        {{- end }}
{{- end -}}
